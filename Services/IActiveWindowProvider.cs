namespace WorkerService1.Services
{
    public interface IActiveWindowProvider
    {
        (int pid, string processName, string title) GetActiveWindowInfo();
    }
}
