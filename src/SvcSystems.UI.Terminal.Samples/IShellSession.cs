namespace SvcSystems.UI.Terminal.Samples;

internal interface IShellSession : IDisposable
{
    event Action<byte[]>? DataReceived;

    event Action<int>? Exited;

    void Start();

    void Send(byte[] input);

    void Resize(int cols, int rows);
}
