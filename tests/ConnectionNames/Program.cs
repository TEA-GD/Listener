using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;

static void Check(bool value,string message){if(!value)throw new Exception(message);}
static async Task<ConnectionNames.NameResult> Wait(ConnectionNames service,string ip){
    var until=DateTime.UtcNow.AddSeconds(5);
    while(DateTime.UtcNow<until){var result=service.Request(IPAddress.Parse(ip));if(result.Status!="pending")return result;await Task.Delay(10);}
    throw new Exception("Lookup did not finish");
}
var lookup=new FakeLookup();using var service=new ConnectionNames(lookup);await service.StartAsync(default);
try{
    Check(service.Request(IPAddress.Parse("127.0.0.1")).Name=="localhost","Loopback name missing");
    Check(service.Request(IPAddress.Any).Status=="unavailable","Wildcard should not be resolved");
    Check(service.Request(IPAddress.Parse("239.1.1.1")).Status=="unavailable","Multicast should not be resolved");
    Check(service.Request(IPAddress.Parse("192.0.2.1")).Status=="pending","Lookup should enqueue without blocking");
    for(int i=0;i<20;i++)service.Request(IPAddress.Parse("192.0.2.1"));
    Check((await Wait(service,"192.0.2.1")).Name=="device.example.local","PTR normalization failed");
    Check(lookup.Calls["192.0.2.1"]==1,"Concurrent requests repeated DNS");
    Check((await Wait(service,"::ffff:192.0.2.1")).Name=="device.example.local"&&lookup.Calls["192.0.2.1"]==1,"Mapped IPv6 cache differs");
    Check((await Wait(service,"192.0.2.2")).Status=="unavailable","DNS failure not handled");
    service.Request(IPAddress.Parse("192.0.2.2"));Check(lookup.Calls["192.0.2.2"]==1,"Negative result not cached");
    Check((await Wait(service,"192.0.2.3")).Status=="unavailable","Slow DNS did not time out");
    Check((await Wait(service,"192.0.2.4")).Status=="unavailable","An IP is not a DNS name");
    Check((await Wait(service,"192.0.2.5")).Status=="unavailable","Invalid hostname accepted");
    var ips=Enumerable.Range(10,30).Select(i=>"192.0.2."+i).ToArray();
    foreach(string ip in ips)service.Request(IPAddress.Parse(ip));
    await Task.WhenAll(ips.Select(ip=>Wait(service,ip)));Check(lookup.Peak<=4,"More than four concurrent lookups");
}
finally{await service.StopAsync(default);}
using(var disabled=new ConnectionNames(lookup,false))Check(disabled.Request(IPAddress.Parse("198.51.100.1")).Status=="unavailable","Disable setting ignored");
using(var bounded=new ConnectionNames(lookup)){
    for(int i=1;i<=256;i++)Check(bounded.Request(IPAddress.Parse($"10.0.{i/256}.{i%256}")).Status=="pending","Queue unexpectedly full");
    Check(bounded.Request(IPAddress.Parse("10.1.0.1")).Status=="busy","Queue not bounded");
}
Console.WriteLine("PASS: asynchronous lookup, name validation, positive/negative cache, deduplication, timeout, four-worker limit, bounded queue and disable setting");

sealed class FakeLookup : IReverseNameLookup
{
    public ConcurrentDictionary<string,int> Calls=new();private int active;public int Peak;
    public async Task<string?> Lookup(IPAddress address,CancellationToken token){
        string ip=address.ToString();Calls.AddOrUpdate(ip,1,(_,count)=>count+1);
        int current=Interlocked.Increment(ref active);int observed;
        do{observed=Peak;if(current<=observed)break;}while(Interlocked.CompareExchange(ref Peak,current,observed)!=observed);
        try{
            await Task.Delay(ip=="192.0.2.3"?Timeout.Infinite:30,token);
            if(ip=="192.0.2.2")throw new SocketException((int)SocketError.HostNotFound);
            return ip=="192.0.2.4"?ip:ip=="192.0.2.5"?"<invalid>":"device.example.local.";
        }
        finally{Interlocked.Decrement(ref active);}
    }
}
