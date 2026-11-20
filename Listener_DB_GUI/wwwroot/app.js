'use strict';
const $=id=>document.getElementById(id);
let computers=[],host=null,page=1,total=0,detailRequest=null,listRequest=null;
const size=50;
let historyDateDefault=true;
let connectionRequest=null,connectionPage=1,connectionTotal=0;
function el(tag,text,cls){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n;}
function timestamp(value){if(!value)return '—';const d=new Date(value);return Number.isNaN(d.getTime())?value:d.toISOString().replace('T',' ').replace('Z',' UTC');}
function error(message){$('error').textContent=message;$('error').hidden=!message;}
async function api(path,signal){const r=await fetch(path,{signal});const data=await r.json();if(!r.ok)throw new Error(data.error||'Request failed.');return data;}
const deviceFolders=[['windows_servers','Win Server'],['linux_servers','Linux Server'],['user_pcs','User PCs'],['project_stands','Project Stand'],['legacy','Unclassified']];
const openFolders=new Set(['legacy']);
function deviceCategory(pc){return deviceFolders.some(([key])=>key===pc.category)?pc.category:'legacy';}
function drawPCs(){
    const filter=$('pcSearch').value.toLowerCase();$('pcs').replaceChildren();$('pcCount').textContent=computers.length;
    let matching=0;
    for(const [category,label] of deviceFolders){
        const members=computers.filter(pc=>deviceCategory(pc)===category);
        if(category==='legacy'&&!members.length)continue;
        const shown=members.filter(pc=>pc.host.toLowerCase().includes(filter));matching+=shown.length;
        const folder=el('details',undefined,'device-folder'+(members.some(pc=>pc.host===host)?' selected-folder':''));folder.dataset.category=category;
        folder.open=filter.length>0?shown.length>0:openFolders.has(category);
        folder.addEventListener('toggle',()=>{if(!filter){if(folder.open)openFolders.add(category);else openFolders.delete(category);}});
        const summary=el('summary');summary.setAttribute('aria-label',label+', '+shown.length+' devices');
        const icon=el('span',undefined,'device-folder-icon');icon.setAttribute('aria-hidden','true');
        summary.append(icon,el('span',label,'device-folder-name'),el('span',filter?shown.length+' / '+members.length:String(members.length),'device-folder-count'));
        const items=el('div',undefined,'device-folder-items');
        for(const pc of shown){
            const b=el('button',undefined,'pc'+(pc.host===host?' selected':''));b.setAttribute('aria-pressed',String(pc.host===host));b.append(el('strong',pc.host),el('span',`${Number(pc.events).toLocaleString()} events · ${pc.processes} processes`),el('span',`Received: ${timestamp(pc.lastReceived)}`));b.onclick=()=>{host=pc.host;page=1;connectionPage=1;$('connectionDay').value=(pc.lastEvent||new Date().toISOString()).slice(0,10);if(historyDateDefault){$('from').value=$('to').value=(pc.lastEvent||new Date().toISOString()).slice(0,10);}openFolders.add(category);resetConnectionReport();drawPCs();ListenerTabs.selectHost(host);ListenerOverview.select(host);if(ListenerTabs.active()==='overview')loadDetail();};
            items.append(b);
        }
        if(!shown.length)items.append(el('p',filter?'No matching devices.':'No devices in this category yet.','device-folder-empty'));
        folder.append(summary,items);$('pcs').append(folder);
    }
    if(!computers.length)$('pcs').append(el('p','No collected events yet. Check the collector service and agent delivery.'));
    else if(!matching)$('pcs').append(el('p','No matching PCs.'));
}
async function loadPCs(){listRequest?.abort();const request=new AbortController();listRequest=request;try{computers=await api('/api/pcs',request.signal);const selected=computers.find(pc=>pc.host===host);if(selected)openFolders.add(deviceCategory(selected));drawPCs();error('');if(host&&computers.some(p=>p.host===host)){if(ListenerTabs.active()==='overview')await Promise.all([loadDetail(),ListenerOverview.refresh()]);}else{host=null;ListenerTabs.clear();ListenerOverview.clear();$('detail').hidden=true;$('empty').hidden=ListenerTabs.active()!=='overview';$('title').textContent='Choose a computer';}}catch(e){if(e.name!=='AbortError')error(e.message);}}
async function loadDetail(){if(!host||ListenerTabs.active()!=='overview')return;detailRequest?.abort();const request=new AbortController();detailRequest=request;$('detail').hidden=ListenerTabs.active()!=='overview';$('empty').hidden=true;$('title').textContent=host;$('subtitle').textContent='Loading collected events…';$('events').replaceChildren();$('stats').replaceChildren();$('groups').replaceChildren();$('prev').disabled=$('next').disabled=true;
const p=new URLSearchParams({host,page,size,q:$('query').value,type:$('type').value,from:$('from').value,to:$('to').value,destination:$('destination').value,protocol:$('protocol').value});
try{const d=await api('/api/pc?'+p,request.signal);error('');total=Number(d.stats.events);$('subtitle').textContent=`Last event: ${timestamp(d.stats.lastEvent)} · All times in UTC`;$('updated').textContent='Updated '+new Date().toLocaleTimeString();
for(const [label,value] of [['Recorded events',total],['Processes',d.stats.processes],['Destinations',d.stats.destinations],['TCP connected events',d.stats.connections||0],['UDP endpoint events',d.stats.udpEndpoints||0]]){const card=el('div',undefined,'card');card.append(el('span',label),el('strong',Number(value).toLocaleString()));$('stats').append(card);}
for(const [key,label] of [['types','Event types'],['processes','Top processes'],['destinations','Top destinations'],['ports','Destination ports'],['localPorts','Local ports']]){const group=el('section',undefined,'group');group.append(el('h2',label));for(const row of d.groups[key]){const line=el('div',undefined,'group-row');line.append(el('span',row.name),el('strong',Number(row.count).toLocaleString()));group.append(line);}if(!d.groups[key].length)group.append(el('p','No data'));$('groups').append(group);}
for(const event of d.events){const tr=el('tr');tr.append(el('td',timestamp(event.TIMESTAMP_UTC)));const type=el('td');type.append(el('span',event.EVENT_TYPE,'tag'));tr.append(type);for(const field of ['PROTOCOL','PROCESS_NAME','SOURCE_IP'])tr.append(el('td',event[field]||'—'));tr.append(ListenerNames.source(event,host),el('td',event.SOURCE_PORT||'—'),el('td',event.RECIEVER_IP||'—'),ListenerNames.destination(event),el('td',event.PORT_USED||'—'),el('td',event.STATE||'—'));const cell=el('td'),button=el('button','View');button.setAttribute('aria-label','View event '+event.EVENT_ID);button.onclick=()=>{$('eventFields').replaceChildren();for(const [key,value]of Object.entries(event))$('eventFields').append(el('dt',key),el('dd',value||'—'));$('eventDialog').showModal();};cell.append(button);tr.append(cell);$('events').append(tr);}
if(!d.events.length){const td=el('td','No events match the current filters.');td.colSpan=12;const tr=el('tr');tr.append(td);$('events').append(tr);}$('resultCount').textContent=total.toLocaleString()+' matching events';$('page').textContent=`Page ${page} of ${Math.max(1,Math.ceil(total/size))}`;$('prev').disabled=page===1;$('next').disabled=page*size>=total;
}catch(e){if(e.name!=='AbortError'){error(e.message);$('subtitle').textContent='Could not load events.';}}}
$('pcSearch').oninput=drawPCs;$('refresh').onclick=loadPCs;$('filters').onsubmit=e=>{e.preventDefault();historyDateDefault=false;page=1;loadDetail();};$('reset').onclick=()=>{HTMLFormElement.prototype.reset.call($('filters'));historyDateDefault=false;page=1;loadDetail();};$('prev').onclick=()=>{if(page>1){page--;loadDetail();}};$('next').onclick=()=>{if(page*size<total){page++;loadDetail();}};$('closeDialog').onclick=()=>$('eventDialog').close();loadPCs();

async function loadConnections(){
    connectionRequest?.abort();
    if(!host)return;
    const request=new AbortController();connectionRequest=request;
    $('connections').replaceChildren();$('connectionSummary').textContent='';$('connectionPage').textContent='';
    $('connectionPrev').disabled=$('connectionNext').disabled=true;
    $('connectionStatus').textContent='Comparing this day against retained history…';
    const params=new URLSearchParams({host,day:$('connectionDay').value,gapDays:$('connectionGap').value,page:connectionPage,protocol:$('connectionProtocol').value});
    try{
        const data=await api('/api/connections?'+params,request.signal);
        connectionTotal=Number(data.summary.total);
        $('connectionSummary').textContent=`${data.summary.newDestinations} new · ${data.summary.returnedDestinations} returned`;
        $('connectionStatus').textContent=connectionTotal?'“New” means no earlier observation is stored. “Returned” means the first observation that day followed at least '+data.gapDays+' days without a recorded observation.':'No new or returning destinations for this PC, day and gap threshold.';
        for(const row of data.destinations){
            const tr=el('tr');tr.append(ListenerNames.source({},host),el('td',row.ip),ListenerNames.destination({RECIEVER_IP:row.ip,RECIEVER_DOMAINNAME:row.reportedName}));
            const kind=el('td');kind.append(el('span',row.classification==='new'?'New in history':'Returned after gap','tag '+(row.classification==='new'?'new-connection':'returned-connection')));tr.append(kind);
            for(const value of [timestamp(row.firstSeen),timestamp(row.previousSeen),row.gapDays===null?'—':row.gapDays,row.ports||'—',row.processes||'—',row.observations])tr.append(el('td',value));
            const cell=el('td'),button=el('button','Show events');button.setAttribute('aria-label','Show events for '+row.ip);
            button.onclick=()=>{$('from').value=data.day;$('to').value=data.day;$('type').value='';$('query').value='';$('destination').value=row.ip;$('protocol').value=$('connectionProtocol').value;page=1;loadDetail();};
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

function resetConnectionReport(){
    connectionRequest?.abort();connectionPage=1;connectionTotal=0;
    $('connections').replaceChildren();$('connectionSummary').textContent='';$('connectionPage').textContent='';
    $('connectionPrev').disabled=$('connectionNext').disabled=true;
    $('connectionStatus').textContent='Choose a day and click Check connections to compare against retained history.';
}
document.addEventListener('listener-tab',event=>{
    if(event.detail==='overview'&&host){loadDetail();ListenerOverview.refresh();}
    else{detailRequest?.abort();connectionRequest?.abort();ListenerOverview.pause();}
});
