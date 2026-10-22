// Run the actual served page against deterministic fixtures in headless Chrome.
const fs=require('fs');
const fixture=fs.readFileSync(process.argv[2],'utf8');
const test=`<script>
const uiRequests=[];const originalUiFetch=window.fetch;
window.fetch=async function(url,options){uiRequests.push({url,options});return originalUiFetch(url,options)};
setTimeout(async()=>{try{
const check=(ok,message)=>{if(!ok)throw Error(message)};
await loadInstallLogs();
check(usageCards.textContent.includes('26')&&usageCards.textContent.includes('320'),'usage totals missing');
check(document.querySelectorAll('.machine-log').length===2,'computer groups missing');
check(document.querySelectorAll('.log-event').length===3,'individual log boundaries missing');
check(usageCards.getBoundingClientRect().top<installLogResult.getBoundingClientRect().top,'totals below logs');
renderInstallLogs([{Machine:'<img src=x onerror=alert(1)>',Detail:'<script>unsafe<\\/script>',Status:'log'}]);
check(!installLogResult.querySelector('img')&&installLogResult.textContent.includes('<img'),'log HTML was not escaped');
logFrom.value='2026-10-15';logTo.value='2026-10-15';await loadInstallLogs();
const query=new URL(uiRequests.filter(r=>r.url.includes('/install-logs')).at(-1).url,'http://localhost');
check(Date.parse(query.searchParams.get('to'))-Date.parse(query.searchParams.get('from'))===86400000,'inclusive end date broken');
const count=uiRequests.length;logFrom.value='2026-10-17';await loadInstallLogs();
check(uiRequests.length===count&&installLogResult.textContent.includes('结束日期'),'reversed range sent to API');
logFrom.value=logTo.value='';await loadInstallLogs();
await selectDriverPolicy('packages/network/net.inf','stable');
check(policyDialog.open&&policyOptions.querySelectorAll('input[type=radio]').length===2,'policy picker did not open');
check(policyOptions.querySelector('input:checked').value==='stable','saved stable choice not restored');
policyOs.value='windows10';await loadPolicyCandidates();
check(uiRequests.at(-1).url.includes('os=windows10'),'OS change did not reload candidates');
policyOptions.querySelector('input[value=latest]').checked=true;await saveSelectedPolicy();
let saved=JSON.parse(uiRequests.findLast(r=>r.url==='/api/admin/release-policies'&&r.options?.method==='POST').options.body);
check(saved.StableHash==='latest'&&saved.LatestHash==='latest','saving stable lost latest selection');
check(!policyDialog.open,'saved picker stayed open');
await selectDriverPolicy('packages/network/net.inf','latest');await saveSelectedPolicy(true);
saved=JSON.parse(uiRequests.findLast(r=>r.url==='/api/admin/release-policies'&&r.options?.method==='POST').options.body);
check(saved.StableHash==='stable'&&saved.LatestHash==='','clearing latest changed stable');
check(msg.textContent.includes('指定稳定版独立生效'),'single configured policy incorrectly described as incomplete');
document.body.setAttribute('data-ui-test','PASS');
}catch(e){document.body.setAttribute('data-ui-test','FAIL: '+e.message)}},800);
</script>`;
fs.writeFileSync(process.argv[3],fixture.replace('</body>',test+'</body>'),'utf8');
