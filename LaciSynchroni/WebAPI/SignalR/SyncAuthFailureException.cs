namespace LaciSynchroni.WebAPI.SignalR;

public class SyncAuthFailureException(string reason) : Exception
{
    public string Reason { get; } = reason;
}
