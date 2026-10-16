namespace XTerm.Graphics;

/// <summary>Whether an animation is running, and what it does when it runs out of frames.</summary>
public enum AnimationState
{
    /// <summary>Not advancing. The current frame stays on screen.</summary>
    Stopped = 1,

    /// <summary>
    /// Advancing, but waiting at the last frame rather than looping.
    /// </summary>
    /// <remarks>
    /// For a client still sending frames. It lets an animation start playing before it has all
    /// arrived, without the first few frames repeating while the rest are still in flight.
    /// </remarks>
    Loading = 2,

    /// <summary>Advancing, and looping back to the first frame at the end.</summary>
    Running = 3
}

/// <summary>One frame of an animation: a whole image's worth of pixels, and how long to show it.</summary>
public sealed class ImageFrame
{
    internal byte[] Data;

    internal ImageFrame(byte[] data, int gapMilliseconds)
    {
        Data = data;
        GapMilliseconds = gapMilliseconds;
    }

    /// <summary>The pixels, BGRA8888 with straight alpha, top row first.</summary>
    public ReadOnlyMemory<byte> Pixels => Data;

    /// <summary>
    /// Milliseconds to wait before moving on. Negative means the frame is never shown.
    /// </summary>
    public int GapMilliseconds { get; internal set; }

    /// <summary>
    /// Whether the frame is skipped past instantly rather than displayed.
    /// </summary>
    /// <remarks>
    /// Not a wasted frame: it is how a client stores base data that later frames compose against --
    /// a static background with a small object moving over it, say -- without that base ever
    /// appearing on its own.
    /// </remarks>
    public bool IsGapless => GapMilliseconds < 0;
}

/// <summary>
/// The frames of an animated image, and where in them it currently is.
/// </summary>
/// <remarks>
/// <para>Held beside <see cref="TerminalImage"/> rather than inside it so that the image's own
/// pixels stay genuinely immutable. A host may still cache a texture against the image and hand it
/// to another thread; what it must additionally watch is <see cref="Serial"/>, which changes
/// whenever the visible pixels do.</para>
/// <para>Nothing here keeps time. The emulator is driven entirely by <c>Write</c> and owns no
/// timer -- acquiring one would put a thread into a library that has none, and the host already has
/// a render loop that knows how long a frame took. So the host calls <see cref="Advance"/> and is
/// told whether anything changed.</para>
/// </remarks>
public sealed class ImageAnimation
{
    /// <summary>What a frame's gap is when the client does not say.</summary>
    /// <remarks>The protocol's figure. The root frame is the exception and defaults to none.</remarks>
    public const int DefaultGapMilliseconds = 40;

    private readonly List<ImageFrame> _frames = new();
    private double _elapsedMilliseconds;
    private int _loopsRemaining;

    internal ImageAnimation(TerminalImage image)
    {
        Image = image;

        // Frame one is the root: the image's own pixels, shared rather than copied. Editing it
        // copies first, so TerminalImage.Pixels never changes under a host that cached it.
        _frames.Add(new ImageFrame(image.PixelArray, gapMilliseconds: 0));
    }

    /// <summary>The image these are frames of.</summary>
    public TerminalImage Image { get; }

    /// <summary>How many frames exist, including the root.</summary>
    public int FrameCount => _frames.Count;

    /// <summary>The frame being shown, 1-based.</summary>
    public int CurrentFrame { get; private set; } = 1;

    /// <summary>Whether the animation is stopped, loading, or running.</summary>
    public AnimationState State { get; private set; } = AnimationState.Stopped;

    /// <summary>
    /// Changes whenever the visible pixels change, so a host can tell a stale texture from a fresh
    /// one without comparing buffers.
    /// </summary>
    public int Serial { get; private set; }

    /// <summary>Total size of every frame, for accounting against an image budget.</summary>
    public long ByteCount
    {
        get
        {
            long total = 0;
            foreach (var frame in _frames)
                total += frame.Data.LongLength;
            return total;
        }
    }

    /// <summary>The pixels of the frame currently being shown.</summary>
    public ReadOnlyMemory<byte> CurrentPixels => _frames[CurrentFrame - 1].Pixels;

    /// <summary>Gets a frame by its 1-based number.</summary>
    public bool TryGetFrame(int number, out ImageFrame frame)
    {
        if (number >= 1 && number <= _frames.Count)
        {
            frame = _frames[number - 1];
            return true;
        }

        frame = null!;
        return false;
    }

    /// <summary>Adds a frame and returns its 1-based number.</summary>
    internal int AddFrame(byte[] pixels, int gapMilliseconds)
    {
        _frames.Add(new ImageFrame(pixels, gapMilliseconds));
        Serial++;
        return _frames.Count;
    }

    /// <summary>
    /// Gets a frame's pixels for editing, copying the root away from the image first.
    /// </summary>
    /// <remarks>
    /// The root frame starts as the image's own array. The moment a client edits it the two have to
    /// part company, or <see cref="TerminalImage.Pixels"/> -- documented as immutable, and possibly
    /// already uploaded as a texture -- would change underneath its holders.
    /// </remarks>
    internal byte[] GetWritableFrame(int number)
    {
        var frame = _frames[number - 1];

        if (ReferenceEquals(frame.Data, Image.PixelArray))
            frame.Data = (byte[])frame.Data.Clone();

        Serial++;
        return frame.Data;
    }

    internal void SetGap(int number, int gapMilliseconds)
    {
        if (number >= 1 && number <= _frames.Count)
            _frames[number - 1].GapMilliseconds = gapMilliseconds;
    }

    /// <summary>Makes a frame current, if it exists.</summary>
    internal bool SetCurrentFrame(int number)
    {
        if (number < 1 || number > _frames.Count)
            return false;

        if (CurrentFrame != number)
        {
            CurrentFrame = number;
            Serial++;
        }

        _elapsedMilliseconds = 0;
        return true;
    }

    /// <summary>
    /// Starts, stops, or restarts the animation.
    /// </summary>
    /// <param name="loops">
    /// The protocol's loop count: 0 leaves it alone, 1 is forever, and any larger number plays that
    /// many minus one.
    /// </param>
    internal void SetState(AnimationState state, int loops)
    {
        // v=0 means the client said nothing, and the default is to loop forever -- as does v=1,
        // which says so outright. Only a larger number is finite, and it plays that many minus one.
        // Reading "unspecified" as "no loops" instead stops every animation after a single pass.
        _loopsRemaining = loops > 1 ? loops - 1 : int.MaxValue;

        // Setting the state always resets the counter, which is what the protocol asks for: an
        // animation stopped and started again plays its full complement rather than resuming a
        // part-spent one.
        State = state;
        _elapsedMilliseconds = 0;
    }

    /// <summary>
    /// Moves the animation on by a slice of real time.
    /// </summary>
    /// <remarks>
    /// <para>Driven by the host's render loop rather than by a timer here. Several frames can fall
    /// inside one slice when the gaps are short or a repaint was late, so this loops rather than
    /// stepping once -- otherwise a slow frame silently slows the animation down.</para>
    /// <para>A gapless frame is stepped straight past without being shown. The guard against a
    /// whole animation of them is the frame count: at worst one pass around, and then it stops
    /// rather than spinning.</para>
    /// </remarks>
    /// <returns>True when the visible frame changed and the host should repaint.</returns>
    public bool Advance(TimeSpan delta)
    {
        if (State == AnimationState.Stopped || _frames.Count < 2 || delta <= TimeSpan.Zero)
            return false;

        _elapsedMilliseconds += delta.TotalMilliseconds;

        var startedOn = CurrentFrame;
        var steps = 0;
        var limit = _frames.Count * 2;

        while (steps++ < limit)
        {
            var gap = EffectiveGap(CurrentFrame);

            // A gapless frame is never displayed, so it does not consume any of the elapsed time.
            if (gap > 0 && _elapsedMilliseconds < gap)
                break;

            if (gap > 0)
                _elapsedMilliseconds -= gap;

            if (!StepToNextFrame())
                break;
        }

        if (CurrentFrame == startedOn)
            return false;

        Serial++;
        return true;
    }

    /// <summary>
    /// The gap actually used for a frame: the client's value, or the protocol's default.
    /// </summary>
    /// <remarks>
    /// Zero means the client never set one. The root frame keeps zero deliberately -- a single
    /// still image must not start advancing merely because a frame was added after it -- so it is
    /// only given the default once it is one frame of several.
    /// </remarks>
    private int EffectiveGap(int number)
    {
        var frame = _frames[number - 1];
        if (frame.GapMilliseconds != 0)
            return frame.GapMilliseconds;

        return DefaultGapMilliseconds;
    }

    /// <summary>Moves to the next frame, looping or waiting according to the state.</summary>
    /// <returns>False when the animation has nowhere further to go.</returns>
    private bool StepToNextFrame()
    {
        if (CurrentFrame < _frames.Count)
        {
            CurrentFrame++;
            return true;
        }

        // Past the last frame.
        if (State == AnimationState.Loading)
        {
            // Wait here for more to arrive rather than repeating what has been shown already.
            _elapsedMilliseconds = 0;
            return false;
        }

        if (_loopsRemaining <= 0)
        {
            State = AnimationState.Stopped;
            return false;
        }

        if (_loopsRemaining != int.MaxValue)
            _loopsRemaining--;

        CurrentFrame = 1;
        return true;
    }

    /// <summary>
    /// Draws one rectangle of pixels onto another, either blended or copied outright.
    /// </summary>
    /// <remarks>
    /// <para>Both frame loading and frame composition come down to this, which is why it is one
    /// function rather than two nearly identical ones. The difference between them is only where
    /// the source pixels come from.</para>
    /// <para>Straight (unpremultiplied) alpha, matching the rest of the graphics path. The blend is
    /// the standard source-over: rounding is done by adding half the divisor before dividing, so a
    /// half-transparent white over black gives 128 rather than 127.</para>
    /// </remarks>
    internal static void Blend(byte[] destination, int destinationWidth, int destinationHeight,
                               ReadOnlySpan<byte> source, int sourceWidth,
                               int sourceX, int sourceY,
                               int destinationX, int destinationY,
                               int width, int height, bool replace)
    {
        const int bpp = TerminalImage.BytesPerPixel;

        for (int row = 0; row < height; row++)
        {
            var toY = destinationY + row;
            if (toY < 0 || toY >= destinationHeight)
                continue;

            for (int col = 0; col < width; col++)
            {
                var toX = destinationX + col;
                if (toX < 0 || toX >= destinationWidth)
                    continue;

                var from = ((sourceY + row) * sourceWidth + sourceX + col) * bpp;
                var to = (toY * destinationWidth + toX) * bpp;

                if (from < 0 || from + bpp > source.Length || to + bpp > destination.Length)
                    continue;

                var alpha = source[from + 3];

                if (replace || alpha == 255)
                {
                    destination[to] = source[from];
                    destination[to + 1] = source[from + 1];
                    destination[to + 2] = source[from + 2];
                    destination[to + 3] = alpha;
                    continue;
                }

                if (alpha == 0)
                    continue;

                var inverse = 255 - alpha;
                var outAlpha = alpha + destination[to + 3] * inverse / 255;

                for (int channel = 0; channel < 3; channel++)
                {
                    var over = source[from + channel] * alpha;
                    var under = destination[to + channel] * destination[to + 3] * inverse / 255;
                    destination[to + channel] = outAlpha == 0
                        ? (byte)0
                        : (byte)Math.Clamp((over + under + outAlpha / 2) / outAlpha, 0, 255);
                }

                destination[to + 3] = (byte)Math.Clamp(outAlpha, 0, 255);
            }
        }
    }
}
