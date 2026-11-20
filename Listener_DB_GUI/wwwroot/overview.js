/* Device overview is independent of history filters and refreshes every ten seconds. */
(()=>{
'use strict';
const $=id=>document.getElementById(id);
const make=(tag,text)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};
const duration=value=>{let s=Math.max(0,Math.floor(value||0));const d=Math.floor(s/86400);s%=86400;const h=Math.floor(s/3600);s%=3600;const m=Math.floor(s/60);return (d?d+'d ':'')+(h?h+'h ':'')+(m?m+'m ':'')+(s%60)+'s';};
const time=value=>value?new Date(value).toISOString().replace('T',' ').replace('Z',' UTC'):'—';
let host=null,page=1,total=0,controller=null;
function clear(){host=null;controller?.abort();$('deviceOverview').hidden=true;}
function chart(data){
    const svg=$('minuteChart');svg.replaceChildren();const ns='http://www.w3.org/2000/svg';
    const add=(tag,attrs,text)=>{const n=document.createElementNS(ns,tag);for(const [k,v] of Object.entries(attrs))n.setAttribute(k,v);if(text!==undefined)n.textContent=text;svg.append(n);return n;};
    const top=20,bottom=180,left=45,width=900,max=Math.max(4,Math.ceil(Math.max(0,...data.buckets.map(b=>b.tcp))/4)*4);
    for(let i=0;i<=4;i++){const y=bottom-(bottom-top)*i/4;add('line',{x1:left,x2:left+width,y1:y,y2:y,class:'chart-grid'});add('text',{x:left-9,y:y+4,'text-anchor':'end',class:'chart-label'},String(Math.round(max*i/4*100)/100));}
    const avgY=bottom-(bottom-top)*data.average_tcp_per_minute/max;
    add('line',{x1:left,x2:left+width,y1:avgY,y2:avgY,class:'chart-average'});
    data.buckets.forEach((bucket,i)=>{
        const height=(bottom-top)*bucket.tcp/max;
        const bar=add('rect',{x:left+i*width/60+2,y:bottom-height,width:width/60-4,height:Math.max(height,1),class:'minute-bar',tabindex:'0'});
        const label=`${time(bucket.minute)}: ${bucket.tcp} new TCP connections; ${bucket.udp} UDP endpoints opened`;
        bar.setAttribute('aria-label',label);const title=document.createElementNS(ns,'title');title.textContent=label;bar.append(title);
    });
    for(const i of [0,15,30,45,59])add('text',{x:left+i*width/60,y:208,class:'chart-label','text-anchor':i===59?'end':'start'},data.buckets[i].minute.slice(11,16));
    $('minuteData').replaceChildren();for(const b of data.buckets){const row=make('tr');for(const value of [time(b.minute),b.tcp,b.udp])row.append(make('td',value));$('minuteData').append(row);}
}
async function refresh(){
    if(!host||window.ListenerTabs?.active()!=='overview')return;controller?.abort();const request=new AbortController();controller=request;const selected=host;
    try{
        const response=await fetch('/api/device-overview?'+new URLSearchParams({host:selected,page}),{signal:request.signal});const data=await response.json();
        if(!response.ok)throw Error(data.error||'Could not load device overview.');
        if(host!==selected||request.signal.aborted)return;
        total=data.total;if(page>Math.max(1,Math.ceil(total/50))){page=Math.max(1,Math.ceil(total/50));return refresh();}
        const messages={fresh:'Recent agent report',stale:'Stale — showing last known connections',snapshot_required:'Complete snapshot unavailable — update Collector, then agent',updating:'New snapshot still arriving — showing previous state',clock_skew:'Device clock differs from the Collector — verify its time'};
        $('overviewStatus').textContent=messages[data.status]||data.status;$('overviewStatus').className='report-state '+data.status;
        $('deviceIPs').textContent=data.device_ips.join(', ')||'No routed local IP reported';
        $('deviceIPLabel').textContent=data.ip_source==='last_reported'?'Device IP · last recorded local address':'Device IP addresses · reported local interfaces';
        const presence=data.availability;
        $('deviceIPs').className='device-ip-'+presence.status;
        $('deviceOnlineStatus').title='Based on agent heartbeats. Last heartbeat: '+time(presence.last_heartbeat_utc)+'; timeout: '+presence.timeout_seconds+' seconds.';
        $('deviceOnlineStatus').textContent=presence.status==='online'?'Online for '+duration(presence.current_online_seconds):presence.status==='offline'?'Offline for '+duration((new Date(presence.generated_utc)-new Date(presence.offline_since_utc))/1000)+' · Last online period: '+duration(presence.last_session_online_seconds):'Status unknown — no usable heartbeat reports';
        $('peerIP').textContent='Collector sees: '+(data.collector_peer_ip||'unavailable')+' (may be NAT or loopback)';
        $('activeTCP').textContent=data.status==='snapshot_required'?'—':data.tcp;
        $('activeUDP').textContent=data.status==='snapshot_required'?'—':data.udp;
        $('averageMinute').textContent=data.average_tcp_per_minute;
        $('overviewTimes').textContent=`Last event: ${time(data.last_event_utc)} · Complete snapshot: ${time(data.snapshot_utc)} · Collector received: ${time(data.last_received_utc)}`;
        $('rateSummary').textContent=`${data.peak_tcp_per_minute} peak/min · ${data.average_tcp_per_minute} average/min`;
        chart(data);$('activeRows').replaceChildren();
        for(const row of data.connections){const tr=make('tr');
            for(const value of [row.PROTOCOL,row.PROCESS_NAME||'Unknown',row.PROCESS_ID||'—',row.SOURCE_IP||'—'])tr.append(make('td',value));
            tr.append(ListenerNames.source(row,host),make('td',row.SOURCE_PORT||'—'),make('td',row.RECIEVER_IP||'Peer not reported'),ListenerNames.destination(row),make('td',row.PORT_USED||'—'),make('td',row.STATE||'—'));$('activeRows').append(tr);}
        if(!data.connections.length){const row=make('tr'),cell=make('td',data.status==='snapshot_required'?'This agent has not sent a complete snapshot yet.':'No connections in the latest reported state.');cell.colSpan=10;row.append(cell);$('activeRows').append(row);}
        $('activePage').textContent=`${total} reported connections · Page ${page} of ${Math.max(1,Math.ceil(total/50))}`;
        $('activePrev').disabled=page===1;$('activeNext').disabled=page*50>=total;
    }catch(e){if(e.name!=='AbortError'){$('overviewStatus').textContent='Overview unavailable: '+e.message;$('overviewStatus').className='report-state stale';$('deviceIPs').className='device-ip-unknown';$('deviceOnlineStatus').textContent='Availability could not be refreshed';}}
    finally{if(controller===request)controller=null;}
}
window.ListenerOverview={select(value){host=value;page=1;total=0;$('deviceOverview').hidden=false;$('deviceIPs').textContent='Loading…';$('deviceIPs').className='device-ip-unknown';$('deviceOnlineStatus').textContent='Checking availability…';$('onlineGraph').href='online.html?host='+encodeURIComponent(value);$('analyzeDevice').href='analyze.html?host='+encodeURIComponent(value);$('peerIP').textContent='';$('activeTCP').textContent=$('activeUDP').textContent=$('averageMinute').textContent='—';$('activeRows').replaceChildren();$('minuteChart').replaceChildren();$('minuteData').replaceChildren();$('overviewTimes').textContent='';$('rateSummary').textContent='';$('overviewStatus').textContent='Loading latest report…';$('overviewStatus').className='report-state';$('activePage').textContent='';$('activePrev').disabled=$('activeNext').disabled=true;refresh();},refresh,pause(){controller?.abort();controller=null;},clear};
$('activePrev').onclick=()=>{if(page>1){page--;refresh();}};
$('activeNext').onclick=()=>{if(page*50<total){page++;refresh();}};
$('overviewRefresh').onclick=refresh;
setInterval(()=>{if(host&&!document.hidden&&!controller&&window.ListenerTabs?.active()==='overview')refresh();},10000);
document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh();});
})();
