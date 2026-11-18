namespace WatcherInstaller;

internal interface IAgentRuntime
{
    bool IsInstalled { get; }
    bool HasTask { get; }
    bool IsRunning { get; }
    void StartTask();
    void EndTask();
    void StopInstalledProcess();
    void Pause(int milliseconds);
}

// Task/process controls deliberately have no configuration-write or uninstall operations.
internal static class AgentLifecycle
{
    internal static void Launch(IAgentRuntime runtime,Action<string> log)
    {
        if(!runtime.IsInstalled||!runtime.HasTask)
            throw new InvalidOperationException("Install the agent first using Install / Update. The installed executable, saved configuration and startup task are required.");
        if(runtime.IsRunning){log("Agent is already running. No second instance was launched.");return;}
        log("Launching the installed agent with its saved agent.json. Use Install / Update to apply changes in the setup fields.");
        runtime.StartTask();
        for(int attempt=0;attempt<20;attempt++)
        {
            if(runtime.IsRunning)
            {
                runtime.Pause(500);
                if(runtime.IsRunning){log("Agent process is running. Verify event delivery in the Collector.");return;}
            }
            runtime.Pause(250);
        }
        throw new IOException("The startup task was requested, but the installed agent did not remain running. Check Task Scheduler's Last Run Result and the saved configuration.");
    }

    internal static void Stop(IAgentRuntime runtime,Action<string> log)
    {
        log("Stopping the installed agent; configuration and queued events will be kept.");
        if(runtime.HasTask)runtime.EndTask();
        runtime.StopInstalledProcess();
        for(int attempt=0;attempt<20;attempt++)
        {
            if(!runtime.IsRunning){log("Agent stopped. Startup task, configuration and queued events are preserved; it can start again at the next Windows startup or with Launch Agent.");return;}
            runtime.Pause(250);
        }
        throw new IOException("The installed agent is still running. Check administrator permissions and the setup log.");
    }
}
