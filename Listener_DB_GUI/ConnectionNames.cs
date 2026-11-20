using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;

internal interface IReverseNameLookup
{
    Task<string?> Lookup(IPAddress address,CancellationToken cancellationToken);
}
internal sealed class ReverseNameLookup : IReverseNameLookup
{
    public async Task<string?> Lookup(IPAddress address,CancellationToken cancellationToken)
        => (await Dns.GetHostEntryAsync(address.ToString(),cancellationToken)).HostName;
}

// DNS never runs in the connection/history request. Bounded workers and cache are shared by viewers.
internal sealed class ConnectionNames(IReverseNameLookup lookup,bool enabled=true) : BackgroundService
{
    internal sealed record NameResult(string? Name,string Status);
    private readonly MemoryCache cache=new(new MemoryCacheOptions { SizeLimit=4096 });
    private readonly Channel<IPAddress> queue=Channel.CreateBounded<IPAddress>(new BoundedChannelOptions(256) { FullMode=BoundedChannelFullMode.Wait });
    private readonly object gate=new();
    internal NameResult Request(IPAddress address)
    {
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        string ip=address.ToString();
        if(IPAddress.IsLoopback(address))return new("localhost","resolved");
        var bytes=address.GetAddressBytes();
        if(address.Equals(IPAddress.Any)||address.Equals(IPAddress.IPv6Any)||address.Equals(IPAddress.Broadcast)||address.IsIPv6Multicast||
           (address.AddressFamily==AddressFamily.InterNetwork&&bytes[0]>=224)||
           (address.AddressFamily==AddressFamily.InterNetworkV6&&address.ScopeId!=0)||!enabled)return new(null,"unavailable");
        lock(gate){
            if(cache.TryGetValue(ip,out NameResult? cached)&&cached is not null)return cached;
            if(!queue.Writer.TryWrite(address))return new(null,"busy");
            var pending=new NameResult(null,"pending");Put(ip,pending,TimeSpan.FromMinutes(5));return pending;
        }
    }
    private void Put(string ip,NameResult result,TimeSpan lifetime)=>cache.Set(ip,result,new MemoryCacheEntryOptions { Size=1,AbsoluteExpirationRelativeToNow=lifetime });
    protected override Task ExecuteAsync(CancellationToken stoppingToken)=>Task.WhenAll(Enumerable.Range(0,4).Select(_=>Worker(stoppingToken)));
    private async Task Worker(CancellationToken stoppingToken)
    {
        try{
            await foreach(var address in queue.Reader.ReadAllAsync(stoppingToken)){
                string? name=null;
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);timeout.CancelAfter(TimeSpan.FromSeconds(1));
                try{
                    name=(await lookup.Lookup(address,timeout.Token).WaitAsync(timeout.Token))?.TrimEnd('.');
                    if(string.IsNullOrWhiteSpace(name)||name.Length>253||IPAddress.TryParse(name,out _)||Uri.CheckHostName(name)!=UriHostNameType.Dns)name=null;
                }
                catch(Exception ex) when(ex is SocketException or OperationCanceledException or ArgumentException){ }
                if(stoppingToken.IsCancellationRequested)break;
                lock(gate)Put(address.ToString(),new(name,name is null?"unavailable":"resolved"),TimeSpan.FromMinutes(name is null?1:10));
            }
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){ }
    }
    public override void Dispose(){base.Dispose();cache.Dispose();}
}
