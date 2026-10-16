namespace XTerm.Parser;

/// <summary>
/// Manages parameters for escape sequences.
/// </summary>
public class Params : ICloneable
{
    private readonly List<int> _params;

    /// <summary>
    /// Every sub-parameter in the sequence, flat. <see cref="_subParamStart"/> says where each
    /// parameter's run begins.
    /// </summary>
    /// <remarks>
    /// Flat rather than a list per parameter because almost no sequence has sub-parameters at all,
    /// and the ones that do have a handful — a list of lists would allocate on every CSI to describe
    /// nothing.
    /// </remarks>
    private readonly List<int> _subParams;

    /// <summary>
    /// Index into <see cref="_subParams"/> where each parameter's sub-parameters begin, and -1 for
    /// a parameter that has none, which is nearly all of them.
    /// </summary>
    private readonly List<int> _subParamStart;

    public int Length => _params.Count;

    /// <summary>
    /// Default constructor.
    /// </summary>
    public Params()
    {
        _params = new List<int>(32);
        _subParams = new List<int>(8);
        _subParamStart = new List<int>(32);
    }

    /// <summary>
    /// Copy constructor for cloning.
    /// </summary>
    public Params(Params other)
    {
        _params = new List<int>(other._params);
        _subParams = new List<int>(other._subParams);
        _subParamStart = new List<int>(other._subParamStart);
    }

    /// <summary>
    /// Gets a parameter at a specific index, or returns default value.
    /// </summary>
    public int GetParam(int index, int defaultValue = 0)
    {
        if (index >= 0 && index < _params.Count)
        {
            var value = _params[index];
            return value == -1 ? defaultValue : value;
        }
        return defaultValue;
    }

    /// <summary>
    /// Adds a parameter.
    /// </summary>
    public void AddParam(int value)
    {
        _params.Add(value);
        _subParamStart.Add(-1);
    }

    /// <summary>
    /// Updates the last parameter value.
    /// </summary>
    public void UpdateLastParam(int value)
    {
        if (_params.Count > 0)
        {
            _params[_params.Count - 1] = value;
        }
        else
        {
            // Through AddParam, which extends BOTH lists. Adding to _params alone leaves
            // _subParamStart a element short, and every sub-parameter lookup indexes it by the
            // parameter's own index -- so the next AddSubParam or GetSubParams would throw. The
            // parser cannot reach this today because entering CsiEntry always seeds a parameter,
            // but this type is public and the invariant is new.
            AddParam(value);
        }
    }

    /// <summary>
    /// Adds a sub-parameter.
    /// </summary>
    /// <summary>
    /// Adds a sub-parameter to the parameter most recently added.
    /// </summary>
    public void AddSubParam(int value)
    {
        if (_params.Count == 0)
        {
            // A sequence beginning with a colon has nothing to attach to. Give it a parameter to
            // belong to rather than dropping it, so CSI :1 m is read as parameter 0 with a
            // sub-parameter rather than vanishing.
            AddParam(0);
        }

        var last = _params.Count - 1;
        if (_subParamStart[last] < 0)
            _subParamStart[last] = _subParams.Count;

        _subParams.Add(value);
    }

    /// <summary>
    /// The sub-parameters of one parameter, or an empty list when it has none.
    /// </summary>
    /// <remarks>
    /// These carry the colon forms: <c>4:3</c> for a curly underline, <c>58:2::r:g:b</c> for an
    /// underline colour, <c>38:2::r:g:b</c> for a foreground. Before this returned anything the
    /// parser discarded such sequences outright, so a program using the colon form of truecolor got
    /// no colour at all.
    /// </remarks>
    public IReadOnlyList<int> GetSubParams(int index)
    {
        if (index < 0 || index >= _params.Count)
            return Array.Empty<int>();

        var start = _subParamStart[index];
        if (start < 0)
            return Array.Empty<int>();

        // Runs are contiguous and in order, so this one ends where the next begins.
        var end = _subParams.Count;
        for (var i = index + 1; i < _subParamStart.Count; i++)
        {
            if (_subParamStart[i] >= 0)
            {
                end = _subParamStart[i];
                break;
            }
        }

        return _subParams.GetRange(start, end - start);
    }

    /// <summary>
    /// Resets the parameters.
    /// </summary>
    public void Reset()
    {
        _params.Clear();
        _subParams.Clear();
        _subParamStart.Clear();
    }

    /// <summary>
    /// Checks if a parameter exists at an index.
    /// </summary>
    public bool HasParam(int index)
    {
        return index >= 0 && index < _params.Count && _params[index] != -1;
    }

    /// <summary>
    /// Gets all parameters as an array.
    /// </summary>
    public int[] ToArray()
    {
        return _params.ToArray();
    }

    /// <summary>
    /// Creates a copy of this Params.
    /// </summary>
    public Params Clone()
    {
        return new Params(this);
    }

    /// <summary>
    /// Explicit interface implementation for ICloneable.
    /// </summary>
    object ICloneable.Clone()
    {
        return Clone();
    }
}
