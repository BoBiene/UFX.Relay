namespace ReverseTunnel.Yarp.Tunnel.Transport;

public sealed class TunnelTransportException : Exception
{
    public TunnelTransportException(
        string message,
        Uri? uri = null,
        int? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Uri = uri;
        StatusCode = statusCode;
        ResponseBody = responseBody ?? string.Empty;
    }

    public Uri? Uri { get; }
    public int? StatusCode { get; }
    public string ResponseBody { get; }
}
