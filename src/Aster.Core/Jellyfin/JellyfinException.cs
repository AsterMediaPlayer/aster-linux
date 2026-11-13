namespace Aster.Core.Jellyfin;

public enum JellyfinErrorKind
{
    InvalidUrl,
    Http,
    NotSignedIn,
    Decoding,
}

/// <summary>
/// A failed Jellyfin request, with a message fit to show the user.
/// </summary>
public sealed class JellyfinException : Exception
{
    public JellyfinErrorKind Kind { get; }

    /// <summary>The HTTP status, for <see cref="JellyfinErrorKind.Http"/>.</summary>
    public int? StatusCode { get; }

    /// <summary>The start of the server's response body, when it sent one.</summary>
    public string? ServerMessage { get; }

    private JellyfinException(JellyfinErrorKind kind, string message, int? statusCode = null, string? serverMessage = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        StatusCode = statusCode;
        ServerMessage = serverMessage;
    }

    public static JellyfinException InvalidUrl() =>
        new(JellyfinErrorKind.InvalidUrl, "That server address is not valid.");

    public static JellyfinException Http(int code, string? message)
    {
        var text = code == 401 ? "Incorrect user name or password."
            : !string.IsNullOrEmpty(message) ? $"Server error {code}: {message}"
            : $"Server error {code}.";
        return new(JellyfinErrorKind.Http, text, code, message);
    }

    /// <summary>A failure another kind of server (Plex) explains in its own words.</summary>
    public static JellyfinException Explained(int? code, string message) =>
        new(JellyfinErrorKind.Http, message, code);

    /// <summary>
    /// Why a server address couldn't be reached, in words that say what to
    /// check: a name that doesn't exist, nothing answering, no network, or a
    /// refused certificate (the Apple app's). Anything else keeps its own words.
    /// </summary>
    public static string Unreachable(Exception error)
    {
        const string NothingAnswered = "Nothing answered at that address. Check it, and that the server is running.";
        if (error is TimeoutException) return NothingAnswered;
        if (error is not HttpRequestException http) return error.Message;
        var socket = FindInner<System.Net.Sockets.SocketException>(error)?.SocketErrorCode;
        if (socket is System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.NetworkDown)
            return "This computer isn't connected to a network.";
        return http.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "No server has that name. Check the address for typos.",
            HttpRequestError.SecureConnectionError => "The server's https:// certificate wasn't accepted. Try its http:// address.",
            HttpRequestError.ConnectionError => NothingAnswered,
            _ => $"Couldn't reach a Jellyfin or Emby server at that address: {error.Message}",
        };
    }

    private static T? FindInner<T>(Exception? error) where T : Exception
    {
        for (; error is not null; error = error.InnerException)
            if (error is T found) return found;
        return null;
    }

    public static JellyfinException NotSignedIn() =>
        new(JellyfinErrorKind.NotSignedIn, "You are not signed in.");

    public static JellyfinException Decoding(string detail, Exception? inner = null) =>
        new(JellyfinErrorKind.Decoding, $"Unexpected server response: {detail}", inner: inner);
}
