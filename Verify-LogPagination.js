const fs=require('fs'),vm=require('vm');
const html=fs.readFileSync(process.argv[2],'utf8');
const fn=html.match(/async function loadInstallLogs\(\)\{[\s\S]*?\nlet logPage=/)[0].replace(/\nlet logPage=$/,'');
const pending=[];
const context={consoleView:{style:{display:'block'}},logQuery:{value:''},logRun:{value:''},logFrom:{value:''},logTo:{value:''},usageCards:{},esc:s=>String(s),renderInstallLogs:()=>{},logPage:1,logGeneration:0,logPageText:{},installLogResult:{},logPrevious:{},logNext:{},api:()=>new Promise(resolve=>pending.push(resolve)),encodeURIComponent};
vm.createContext(context);vm.runInContext(fn,context);
(async()=>{
const first=context.loadInstallLogs();context.logPage=2;const second=context.loadInstallLogs();
const response=page=>({page,pages:3,total:43,pageSize:20,machines:1,hardware:2,updates:2,items:[]});
pending[1](response(2));await second;pending[0](response(1));await first;
if(context.logPage!==2||!context.logPageText.textContent.includes('2 / 3'))throw Error('Stale response overwrote requested page');
context.logPage=99;const last=context.loadInstallLogs();pending[2](response(3));await last;
if(context.logPage!==3||!context.logNext.disabled||context.logPrevious.disabled)throw Error('Pagination boundaries incorrect');
console.log('PASS: served log UI ignores stale responses and enforces last-page navigation');
})().catch(e=>{console.error(e);process.exitCode=1});
