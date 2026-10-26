namespace EagleTunnelApi.Application.Panel;

public class PanelApiException(string message, Exception? innerException = null)
    : Exception(message, innerException);
