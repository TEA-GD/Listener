'use strict';
const $=id=>document.getElementById(id);
let computers=[],host=null,page=1,total=0,detailRequest=null,listRequest=null;
const size=50;
let connectionRequest=null,connectionPage=1,connectionTotal=0;
function el(tag,text,cls){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n;}
function timestamp(value){if(!value)return '—';const d=new Date(value);return Number.isNaN(d.getTime())?value:d.toISOString().replace('T',' ').replace('Z',' UTC');}
function error(message){$('error').textContent=message;$('error').hidden=!message;}
async function api(path,signal){const r=await fetch(path,{signal});const data=await r.json();if(!r.ok)throw new Error(data.error||'Request failed.');return data;}
function drawPCs(){const filter=$('pcSearch').value.toLowerCase();$('pcs').replaceChildren();$('pcCount').textContent=computers.length;for(const pc of computers.filter(p=>p.host.toLowerCase().includes(filter))){const b=el('button',undefined,'pc'+(pc.host===host?' selected':''));b.setAttribute('aria-pressed',String(pc.host===host));b.append(el('strong',pc.host),el('span',`${Number(pc.events).toLocaleString()} events · ${pc.processes} processes`),el('span',`Received: ${timestamp(pc.lastReceived)}`));b.onclick=()=>{host=pc.host;page=1;connectionPage=1;$('connectionDay').value=(pc.lastEvent||new Date().toISOString()).slice(0,10);drawPCs();loadDetail();loadConnections();};$('pcs').append(b);}if(!$('pcs').children.length)$('pcs').append(el('p',computers.length?'No matching PCs.':'No collected events yet. Check the collector service and agent delivery.'));}
async function loadPCs(){listRequest?.abort();const request=new AbortController();listRequest=request;try{computers=await api('/api/pcs',request.signal);drawPCs();error('');if(host&&computers.some(p=>p.host===host)){await Promise.all([loadDetail(),loadConnections()]);}else{host=null;$('detail').hidden=true;$('empty').hidden=false;$('title').textContent='Choose a computer';}}catch(e){if(e.name!=='AbortError')error(e.message);}}
async function loadDetail(){if(!host)return;detailRequest?.abort();const request=new AbortController();detailRequest=request;$('detail').hidden=false;$('empty').hidden=true;$('title').textContent=host;$('subtitle').textContent='Loading collected events…';$('events').replaceChildren();$('stats').replaceChildren();$('groups').replaceChildren();$('prev').disabled=$('next').disabled=true;
const p=new URLSearchParams({host,page,size,q:$('query').value,type:$('type').value,from:$('from').value,to:$('to').value,destination:$('destination').value});
try{const d=await api('/api/pc?'+p,request.signal);error('');total=Number(d.stats.events);$('subtitle').textContent=`Last event: ${timestamp(d.stats.lastEvent)} · All times in UTC`;$('updated').textContent='Updated '+new Date().toLocaleTimeString();
for(const [label,value] of [['Recorded events',total],['Processes',d.stats.processes],['Destinations',d.stats.destinations],['Connected events',d.stats.connections||0]]){const card=el('div',undefined,'card');card.append(el('span',label),el('strong',Number(value).toLocaleString()));$('stats').append(card);}
for(const [key,label] of [['types','Event types'],['processes','Top processes'],['destinations','Top destinations'],['ports','Top ports']]){const group=el('section',undefined,'group');group.append(el('h2',label));for(const row of d.groups[key]){const line=el('div',undefined,'group-row');line.append(el('span',row.name),el('strong',Number(row.count).toLocaleString()));group.append(line);}if(!d.groups[key].length)group.append(el('p','No data'));$('groups').append(group);}
for(const event of d.events){const tr=el('tr');tr.append(el('td',timestamp(event.TIMESTAMP_UTC)));const type=el('td');type.append(el('span',event.EVENT_TYPE,'tag'));tr.append(type);for(const field of ['PROCESS_NAME','SOURCE_IP','RECIEVER_IP','PORT_USED','STATE'])tr.append(el('td',event[field]||'—'));const cell=el('td'),button=el('button','View');button.setAttribute('aria-label','View event '+event.EVENT_ID);button.onclick=()=>{$('eventFields').replaceChildren();for(const [key,value]of Object.entries(event))$('eventFields').append(el('dt',key),el('dd',value||'—'));$('eventDialog').showModal();};cell.append(button);tr.append(cell);$('events').append(tr);}
if(!d.events.length){const td=el('td','No events match the current filters.');td.colSpan=8;const tr=el('tr');tr.append(td);$('events').append(tr);}$('resultCount').textContent=total.toLocaleString()+' matching events';$('page').textContent=`Page ${page} of ${Math.max(1,Math.ceil(total/size))}`;$('prev').disabled=page===1;$('next').disabled=page*size>=total;
}catch(e){if(e.name!=='AbortError'){error(e.message);$('subtitle').textContent='Could not load events.';}}}
$('pcSearch').oninput=drawPCs;$('refresh').onclick=loadPCs;$('filters').onsubmit=e=>{e.preventDefault();page=1;loadDetail();};$('reset').onclick=()=>{$('filters').reset();page=1;loadDetail();};$('prev').onclick=()=>{if(page>1){page--;loadDetail();}};$('next').onclick=()=>{if(page*size<total){page++;loadDetail();}};$('closeDialog').onclick=()=>$('eventDialog').close();loadPCs();

async function loadConnections(){
    connectionRequest?.abort();
    if(!host)return;
    const request=new AbortController();connectionRequest=request;
    $('connections').replaceChildren();$('connectionSummary').textContent='';$('connectionPage').textContent='';
    $('connectionPrev').disabled=$('connectionNext').disabled=true;
    $('connectionStatus').textContent='Comparing this day against retained history…';
    const params=new URLSearchParams({host,day:$('connectionDay').value,gapDays:$('connectionGap').value,page:connectionPage});
    try{
        const data=await api('/api/connections?'+params,request.signal);
        connectionTotal=Number(data.summary.total);
        $('connectionSummary').textContent=`${data.summary.newDestinations} new · ${data.summary.returnedDestinations} returned`;
        $('connectionStatus').textContent=connectionTotal?'“New” means no earlier observation is stored. “Returned” means the first observation that day followed at least '+data.gapDays+' days without a recorded observation.':'No new or returning destinations for this PC, day and gap threshold.';
        for(const row of data.destinations){
            const tr=el('tr');tr.append(el('td',row.ip));
            const kind=el('td');kind.append(el('span',row.classification==='new'?'New in history':'Returned after gap','tag '+(row.classification==='new'?'new-connection':'returned-connection')));tr.append(kind);
            for(const value of [timestamp(row.firstSeen),timestamp(row.previousSeen),row.gapDays===null?'—':row.gapDays,row.ports||'—',row.processes||'—',row.observations])tr.append(el('td',value));
            const cell=el('td'),button=el('button','Show events');button.setAttribute('aria-label','Show events for '+row.ip);
            button.onclick=()=>{$('from').value=data.day;$('to').value=data.day;$('type').value='';$('query').value='';$('destination').value=row.ip;page=1;loadDetail();};
            cell.append(button);tr.append(cell);$('connections').append(tr);
        }
        $('connectionPage').textContent=`Page ${connectionPage} of ${Math.max(1,Math.ceil(connectionTotal/data.size))}`;
        $('connectionPrev').disabled=connectionPage===1;$('connectionNext').disabled=connectionPage*data.size>=connectionTotal;
    }catch(e){if(e.name!=='AbortError')$('connectionStatus').textContent='Could not check connections: '+e.message;}
}
$('connectionDay').value=new Date().toISOString().slice(0,10);
$('connectionFilters').onsubmit=e=>{e.preventDefault();connectionPage=1;loadConnections();};
$('connectionPrev').onclick=()=>{if(connectionPage>1){connectionPage--;loadConnections();}};
$('connectionNext').onclick=()=>{if(connectionPage*50<connectionTotal){connectionPage++;loadConnections();}};
