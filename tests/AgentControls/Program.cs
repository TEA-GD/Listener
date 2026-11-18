using WatcherInstaller;

static void Check(bool value,string message){if(!value)throw new Exception(message);}
var log=new List<string>();
var running=new FakeRuntime{IsRunning=true};
AgentLifecycle.Launch(running,log.Add);
Check(running.StartCalls==0&&running.StopCalls==0,"Launch duplicated/restarted an already running agent");
var stopped=new FakeRuntime();AgentLifecycle.Launch(stopped,log.Add);
Check(stopped.IsRunning&&stopped.StartCalls==1&&stopped.StopCalls==0,"Launch did not start the existing task");
AgentLifecycle.Stop(stopped,log.Add);
Check(!stopped.IsRunning&&stopped.EndCalls==1&&stopped.StopCalls==1,"Stop did not end the task and installed process");
Check(stopped.HasTask&&stopped.IsInstalled,"Stop removed installation/task");
AgentLifecycle.Launch(stopped,log.Add);
Check(stopped.IsRunning&&stopped.StartCalls==2,"Stopped agent cannot be launched again");
var missing=new FakeRuntime{IsInstalled=false,HasTask=false};
bool rejected=false;try{AgentLifecycle.Launch(missing,log.Add);}catch(InvalidOperationException){rejected=true;}
Check(rejected&&missing.StartCalls==0,"Launch did not reject an uninstalled agent");
AgentLifecycle.Stop(missing,log.Add);Check(missing.EndCalls==0,"Stop tried to end a missing task");
var failed=new FakeRuntime{FailStart=true};
rejected=false;try{AgentLifecycle.Launch(failed,log.Add);}catch(IOException){rejected=true;}
Check(rejected,"Launch reported success when the process never started");
var stubborn=new FakeRuntime{IsRunning=true,FailStop=true};
rejected=false;try{AgentLifecycle.Stop(stubborn,log.Add);}catch(IOException){rejected=true;}
Check(rejected,"Stop reported success while the process was still running");
Console.WriteLine("PASS: launch, already-running guard, stop/relaunch, missing installation, startup failure and stop verification");

sealed class FakeRuntime:IAgentRuntime
{
    public bool IsInstalled{get;set;}=true;
    public bool HasTask{get;set;}=true;
    public bool IsRunning{get;set;}
    public bool FailStart,FailStop;
    public int StartCalls,EndCalls,StopCalls;
    public void StartTask(){StartCalls++;if(!FailStart)IsRunning=true;}
    public void EndTask(){EndCalls++;}
    public void StopInstalledProcess(){StopCalls++;if(!FailStop)IsRunning=false;}
    public void Pause(int milliseconds){}
}
