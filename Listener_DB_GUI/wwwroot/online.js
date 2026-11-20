(()=>{
'use strict';
const $=id=>document.getElementById(id),host=new URLSearchParams(location.search).get('host');
const make=(tag,text)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
let controller=null,loaded=false,applied=null;
function duration(value){let s=Math.max(0,Math.floor(value||0));const d=Math.floor(s/86400);s%=86400;const h=Math.floor(s/3600);s%=3600;const m=Math.floor(s/60);return (d?d+'d ':'')+(h?h+'h ':'')+(m?m+'m ':'')+(s%60)+'s';}
function dateInZone(date,zone){const parts=new Intl.DateTimeFormat('en-CA',{timeZone:zone,year:'numeric',month:'2-digit',day:'2-digit'}).formatToParts(date);return ['year','month','day'].map(type=>parts.find(p=>p.type===type).value).join('-');}
function local(value,zone){return value?new Intl.DateTimeFormat('en-GB',{timeZone:zone,dateStyle:'medium',timeStyle:'medium'}).format(new Date(value)):'—';}
function error(message){$('onlineError').textContent=message;$('onlineError').hidden=!message;}
function defaults(){const zone=$('onlineTimezone').value;const end=dateInZone(new Date(),zone),start=new Date(end+'T00:00:00Z');start.setUTCDate(start.getUTCDate()-6);$('onlineFrom').value=start.toISOString().slice(0,10);$('onlineTo').value=end;$('onlineTo').max=end;}
$('onlineTimezone').value=Intl.DateTimeFormat().resolvedOptions().timeZone||'UTC';defaults();
if(host)$('onlineTitle').textContent=host+' · Online history';
function draw(data){
    const summary=data.summary;
    $('onlineState').textContent=summary.status==='online'?'Online':summary.status==='offline'?'Offline':'Unknown';$('onlineState').className='device-ip-'+summary.status;
    $('onlineCurrentPeriod').textContent=summary.status==='online'?'Online for '+duration(summary.current_online_seconds)+' · since '+local(summary.online_since_utc,data.timezone):summary.status==='offline'?'No recent report since '+local(summary.offline_since_utc,data.timezone):'No usable retained heartbeat reports';
    $('onlineRangeTotal').textContent=duration(data.total_online_seconds);$('bufferedRangeTotal').textContent=duration(data.total_buffered_seconds);$('offlineRangeTotal').textContent=duration(data.total_offline_seconds);
    $('onlineLastPeriod').textContent=summary.last_session_start_utc?duration(summary.last_session_online_seconds):'—';$('onlineLastHeartbeat').textContent='Last heartbeat: '+local(summary.last_heartbeat_utc,data.timezone);
    $('onlineSubtitle').textContent='Reported availability in '+data.timezone+' · refreshed '+local(data.generated_utc,data.timezone);
    $('onlineMethod').textContent='A heartbeat keeps a device online for '+summary.timeout_seconds+' seconds. Orange means heartbeat delivery was delayed by more than '+summary.delivery_delay_seconds+' seconds; it can only be reconstructed after buffered reports arrive. Current and last online periods use retained history; the selected date range controls the daily chart and totals. Up to 90 days per view.';
    $('onlineTimelineSummary').textContent=data.from+' → '+data.to+' · '+data.timezone;
    $('onlineTimeline').replaceChildren();$('onlinePeriods').replaceChildren();
    const ns='http://www.w3.org/2000/svg';
    for(const day of data.days){
        const row=make('section');row.className='online-day';
        const caption=make('div');caption.className='online-day-caption';caption.append(make('strong',day.date),make('span',duration(day.online_seconds)+' working · '+duration(day.buffered_seconds)+' delayed'));row.append(caption);
        const scroll=make('div');scroll.className='online-day-scroll';const svg=document.createElementNS(ns,'svg');svg.setAttribute('viewBox','0 0 1000 64');svg.setAttribute('role','img');svg.setAttribute('aria-label',day.date+': '+duration(day.online_seconds)+' working · '+duration(day.buffered_seconds)+' delayed');
        const add=(tag,attrs,text)=>{const n=document.createElementNS(ns,tag);for(const [key,value] of Object.entries(attrs))n.setAttribute(key,value);if(text!==undefined)n.textContent=text;svg.append(n);return n;};
        const full=Math.max(1,day.day_seconds);
        for(const segment of day.segments){
            const rect=add('rect',{x:20+segment.offset_seconds/full*960,y:12,width:Math.max(.4,segment.duration_seconds/full*960),height:24,class:'availability-segment '+segment.status,tabindex:0});
            const label=(segment.status==='buffered'?'Working; reports delivered late':segment.status==='online'?'Working; timely reports (estimated)':segment.status==='offline'?'Offline / no recent reports':segment.status==='future'?'Future time':'Unknown before first retained report')+' · '+segment.start_local+' → '+segment.end_local+' · '+duration(segment.duration_seconds);
            rect.setAttribute('aria-label',label);const title=document.createElementNS(ns,'title');title.textContent=label;rect.append(title);
            if(segment.status==='online'||segment.status==='buffered'){const tr=make('tr');for(const value of [day.date,segment.start_local,segment.end_local,duration(segment.duration_seconds),segment.status==='buffered'?'Buffered / delayed':'Timely reports'])tr.append(make('td',value));$('onlinePeriods').append(tr);}
        }
        for(let i=0;i<day.ticks.length;i++){
            const tick=day.ticks[i],x=20+tick.offset_seconds/full*960;
            add('line',{x1:x,x2:x,y1:38,y2:43,class:'availability-tick'});
            if(i%3===0||i===day.ticks.length-1)add('text',{x,y:58,'text-anchor':i===0?'start':i===day.ticks.length-1?'end':'middle',class:'chart-label'},tick.label);
        }
        scroll.append(svg);row.append(scroll);$('onlineTimeline').append(row);
    }
    if(!$('onlinePeriods').children.length){const tr=make('tr'),td=make('td','No reported online periods in these dates.');td.colSpan=5;tr.append(td);$('onlinePeriods').append(tr);}
}
async function load(auto=false){
    if(!host){error('Choose a device in the main dashboard and click Show online graph.');return;}
    controller?.abort();const request=new AbortController();controller=request;
    try{
        const values=auto&&applied?applied:{host,from:$('onlineFrom').value,to:$('onlineTo').value,tz:$('onlineTimezone').value};
        const today=dateInZone(new Date(),values.tz);if(!auto)$('onlineTo').max=today;
        const query=new URLSearchParams(values);
        const response=await fetch('/api/availability?'+query,{signal:request.signal});const data=await response.json();if(!response.ok)throw Error(data.error||'Could not load online history.');if(request.signal.aborted)return;error('');draw(data);applied={...values};loaded=true;
    }catch(e){if(e.name!=='AbortError'){error(e.message);$('onlineState').textContent='Unavailable';$('onlineState').className='device-ip-unknown';}}
}
$('onlineFilters').onsubmit=e=>{e.preventDefault();load();};$('onlineRefresh').onclick=()=>load();
$('onlineTimezone').onchange=()=>{try{$('onlineTo').max=dateInZone(new Date(),$('onlineTimezone').value);}catch(e){error('Choose a valid time zone, such as UTC or Europe/Moscow.');}};
setInterval(()=>{if(loaded&&!document.hidden)load(true);},10000);load();
})();
