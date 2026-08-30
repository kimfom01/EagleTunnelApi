namespace EagleTunnelApi.Webhook.Exceptions;

public class PanelApiException(string message, Exception? innerException = null)
    : Exception(message, innerException);
