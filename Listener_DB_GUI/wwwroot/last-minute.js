/* A captured frame stays frozen, including while paging, until explicitly recaptured. */
(()=>{
'use strict';
const $=id=>document.getElementById(id);
const make=(tag,text)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};
const stamp=value=>value?new Date(value).toISOString().replace('T',' ').replace('Z',' UTC'):'—';
let host=null,active='overview',page=1,total=0,at=null,watermark=null,controller=null;
const messages={fresh:'Recent agent report',stale:'Stale agent report — this frame may be incomplete',snapshot_required:'No complete device snapshot — showing only recorded observations',updating:'A newer snapshot is incomplete — some current states are unknown',clock_skew:'Device clock differs — future-dated observations are excluded'};
function renderTabs(){
    for(const name of ['overview','minute']){const selected=active===name;$(name+'Tab').setAttribute('aria-selected',String(selected));$(name+'Tab').tabIndex=selected?0:-1;}
    $('detail').hidden=active!=='overview'||!host;
    $('minutePanel').hidden=active!=='minute';
    $('empty').hidden=active!=='overview'||!!host;
}
function reset(){
    controller?.abort();page=1;total=0;at=null;watermark=null;
    $('minuteRows').replaceChildren();$('minuteTimes').textContent='';$('minuteReport').textContent='';$('minutePage').textContent='';
    $('minuteTotal').textContent=$('minutePresent').textContent=$('minuteClosed').textContent='—';
    $('minuteIPs').textContent='—';$('minutePeer').textContent='';
    $('minutePrev').disabled=$('minuteNext').disabled=true;
    $('minuteCapture').disabled=!host;$('minuteDevice').textContent=host||'Choose a device';
    $('minuteStatus').textContent=host?'Capture the last minute to inspect its connections.':'Select a device on the left, then capture its last minute.';
    $('minuteStatus').className='report-state';
}
async function capture(fresh=true){
    if(!host)return;
    controller?.abort();const request=new AbortController();controller=request;const selected=host;
    if(fresh){page=1;at=null;watermark=null;$('minuteRows').replaceChildren();$('minuteTimes').textContent='Capturing…';$('minuteReport').textContent='';$('minuteTotal').textContent=$('minutePresent').textContent=$('minuteClosed').textContent='—';$('minuteIPs').textContent='—';$('minutePeer').textContent='';}
    $('minuteCapture').disabled=true;$('minutePrev').disabled=$('minuteNext').disabled=true;
    const params=new URLSearchParams({host:selected,page});if(at!==null){params.set('at',at);params.set('watermark',watermark);}
    try{
        const response=await fetch('/api/last-minute?'+params,{signal:request.signal});const data=await response.json();
        if(!response.ok)throw Error(data.error||'Could not capture this device.');
        if(request.signal.aborted||host!==selected)return;
        at=data.captured_utc;watermark=data.watermark;total=data.total;
        $('minuteStatus').textContent=messages[data.status]||data.status;$('minuteStatus').className='report-state '+data.status;
        $('minuteTimes').textContent=`Frozen capture: ${stamp(data.captured_utc)} · Window: ${stamp(data.from_utc)} → ${stamp(data.captured_utc)}`;
        $('minuteReport').textContent=`Complete device snapshot: ${stamp(data.snapshot_utc)} · Last event: ${stamp(data.last_event_utc)} · Collector received: ${stamp(data.last_received_utc)}`;
        $('minuteTotal').textContent=data.total;$('minutePresent').textContent=data.present;$('minuteClosed').textContent=data.closed;
        $('minuteIPs').textContent=data.device_ips.join(', ')||'No routed local IP reported';$('minutePeer').textContent='Collector sees: '+(data.collector_peer_ip||'unavailable');
        $('minuteRows').replaceChildren();
        for(const row of data.connections){
            const tr=make('tr');for(const value of [row.PROTOCOL||'—',row.PROCESS_NAME||'Unknown',row.PROCESS_ID||'—',row.SOURCE_IP||'—',row.SOURCE_PORT||'—',row.RECIEVER_IP||'Peer not reported',row.PORT_USED||'—'])tr.append(make('td',value));
            const state=make('td');const tag=make('span',row.window_status);tag.className='tag '+(row.present_at_latest?'new-connection':'returned-connection');state.append(tag);tr.append(state);
            tr.append(make('td',stamp(row.last_observed_utc)));$('minuteRows').append(tr);
        }
        if(!data.connections.length){const tr=make('tr'),td=make('td','No connections were reported for this device in the captured minute. Missing reports do not prove the device was idle.');td.colSpan=9;tr.append(td);$('minuteRows').append(tr);}
        $('minutePage').textContent=`${total} unique connections · Page ${page} of ${Math.max(1,Math.ceil(total/data.size))}`;
        $('minutePrev').disabled=page===1;$('minuteNext').disabled=page*data.size>=total;
    }catch(e){if(e.name!=='AbortError'){$('minuteStatus').textContent='Capture unavailable: '+e.message;$('minuteStatus').className='report-state stale';if(fresh){$('minuteRows').replaceChildren();$('minuteTimes').textContent='No new frame captured.';}}}
    finally{if(controller===request)$('minuteCapture').disabled=!host;}
}
function selectTab(name){active=name;renderTabs();document.dispatchEvent(new CustomEvent('listener-tab',{detail:name}));if(name==='minute'&&host&&at===null)capture();}
window.ListenerTabs={active:()=>active,selectHost(value){if(value!==host){host=value;reset();}renderTabs();if(active==='minute'&&host&&at===null)capture();},clear(){host=null;reset();renderTabs();}};
$('overviewTab').onclick=()=>selectTab('overview');$('minuteTab').onclick=()=>selectTab('minute');
$('mainTabs').onkeydown=e=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;e.preventDefault();selectTab(e.key==='Home'?'overview':e.key==='End'?'minute':active==='overview'?'minute':'overview');$(active+'Tab').focus();};
$('minuteCapture').onclick=()=>capture();$('minutePrev').onclick=()=>{if(page>1){page--;capture(false);}};$('minuteNext').onclick=()=>{if(page*50<total){page++;capture(false);}};
reset();renderTabs();
})();
