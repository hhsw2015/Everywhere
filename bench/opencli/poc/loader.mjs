// Node ESM loader hook — rewrites the same set of specifiers our C#
// OpenCliDocumentLoader rewrites, so the bench PoC and the production
// runtime share one source of shim truth.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const HERE = new URL('./', import.meta.url).href;

// `data:` URLs let us inline shim source without committing extra .mjs
// files; one entry per specifier we shim.
function inline(src) { return 'data:text/javascript;base64,' + Buffer.from(src, 'utf8').toString('base64'); }

// Read the host shim once so we get the EXACT same JS the C# runtime
// injects.
const HOST = readFileSync(fileURLToPath(new URL('./host.mjs', import.meta.url)), 'utf8');
// Stub modules — match HostShim.cs sources.
const NODE_PATH = `
const sep = '/'; const delimiter = ':';
function normalize(p){if(!p)return '.';const isAbs=p.startsWith('/');const trail=p.endsWith('/');const parts=p.split('/').filter(Boolean);const out=[];for(const x of parts){if(x==='.')continue;if(x==='..'){if(out.length&&out.at(-1)!=='..')out.pop();else if(!isAbs)out.push('..');}else out.push(x);}let r=out.join('/');if(isAbs)r='/'+r;if(trail&&r&&!r.endsWith('/'))r+='/';return r||(isAbs?'/':'.');}
function join(...p){const f=p.filter(x=>typeof x==='string'&&x.length);if(!f.length)return '.';return normalize(f.join('/'));}
function resolve(...p){let r='';let abs=false;for(let i=p.length-1;i>=0&&!abs;i--){const x=p[i];if(typeof x!=='string'||!x)continue;r=x+'/'+r;abs=x.startsWith('/');}if(!abs)r='/'+r;return normalize(r).replace(/\\/$/, '')||'/';}
function dirname(p){if(!p)return '.';const i=p.lastIndexOf('/');if(i<0)return '.';if(i===0)return '/';return p.slice(0,i);}
function basename(p,e){if(!p)return '';const i=p.lastIndexOf('/');let b=i>=0?p.slice(i+1):p;if(e&&b.endsWith(e))b=b.slice(0,-e.length);return b;}
function extname(p){if(!p)return '';const b=basename(p);const i=b.lastIndexOf('.');return i<=0?'':b.slice(i);}
function isAbsolute(p){return typeof p==='string'&&p.startsWith('/');}
function relative(f,t){const a=resolve(f).split('/').filter(Boolean);const b=resolve(t).split('/').filter(Boolean);let i=0;while(i<a.length&&i<b.length&&a[i]===b[i])i++;return [...Array(a.length-i).fill('..'),...b.slice(i)].join('/')||'.';}
function parse(p){const root=isAbsolute(p)?'/':'';const dir=dirname(p);const base=basename(p);const ext=extname(base);const name=ext?base.slice(0,-ext.length):base;return {root,dir,base,name,ext};}
function format(o){const dir=o.dir||o.root||'';const base=o.base||((o.name||'')+(o.ext||''));return dir?(dir.endsWith('/')?dir+base:dir+'/'+base):base;}
export {sep,delimiter,normalize,join,resolve,dirname,basename,extname,isAbsolute,relative,parse,format};
export default {sep,delimiter,normalize,join,resolve,dirname,basename,extname,isAbsolute,relative,parse,format};
`;
const NODE_OS = `
const platform=()=>'darwin';const arch=()=>'arm64';const tmpdir=()=>'/tmp';const homedir=()=>'/';const EOL='\\n';const hostname=()=>'opencli';const cpus=()=>[];const totalmem=()=>0;const freemem=()=>0;const networkInterfaces=()=>({});
export {platform,arch,tmpdir,homedir,EOL,hostname,cpus,totalmem,freemem,networkInterfaces};
export default {platform,arch,tmpdir,homedir,EOL,hostname,cpus,totalmem,freemem,networkInterfaces};
`;
const NODE_CRYPTO = `
import { createHash as nh, createHmac as nhm, randomBytes as nrb, randomUUID as nu } from 'node:crypto';
export const createHash=nh; export const createHmac=nhm; export const randomBytes=nrb; export const randomUUID=nu;
export default { createHash:nh, createHmac:nhm, randomBytes:nrb, randomUUID:nu };
`;
const NODE_FS = `
const _u=n=>()=>{throw new Error('fs.'+n+' is not available in the embedded runtime');};
export const readFileSync=_u('readFileSync'); export const readFile=_u('readFile');
export const writeFileSync=_u('writeFileSync'); export const writeFile=_u('writeFile');
export const existsSync=()=>false; export const mkdirSync=_u('mkdirSync');
export const stat=_u('stat'); export const statSync=_u('statSync');
export const promises={readFile:_u('promises.readFile'),writeFile:_u('promises.writeFile'),mkdir:_u('promises.mkdir'),stat:_u('promises.stat')};
export const createReadStream=_u('createReadStream'); export const createWriteStream=_u('createWriteStream');
export default {readFileSync,readFile,writeFileSync,writeFile,existsSync,mkdirSync,stat,statSync,promises,createReadStream,createWriteStream};
`;
const NODE_CHILD = `
const _u=n=>()=>{throw new Error('child_process.'+n+' is not available in the embedded runtime');};
export const exec=_u('exec'); export const execSync=_u('execSync');
export const execFile=_u('execFile'); export const execFileSync=_u('execFileSync');
export const spawn=_u('spawn'); export const spawnSync=_u('spawnSync');
export const fork=_u('fork');
export default {exec,execSync,execFile,execFileSync,spawn,spawnSync,fork};
`;
const NODE_HTTP = `
const _u=n=>()=>{throw new Error('http(s).'+n+' is not available in the embedded runtime; use global fetch instead');};
export const request=_u('request'); export const get=_u('get'); export const createServer=_u('createServer');
export default {request,get,createServer};
`;
const UTILS = `
const delay=(ms)=>new Promise(r=>setTimeout(r,ms));const sleep=delay;
const range=(n)=>Array.from({length:n},(_,i)=>i);
const chunk=(a,n)=>{const o=[];for(let i=0;i<a.length;i+=n)o.push(a.slice(i,i+n));return o;};
const unique=(a)=>Array.from(new Set(a));
const compact=(a)=>a.filter(x=>x!=null);
const last=(a)=>a.length?a[a.length-1]:undefined;
const first=(a)=>a.length?a[0]:undefined;
const noop=()=>{};
const isRecord=(v)=>v!==null&&typeof v==='object'&&!Array.isArray(v);
const isString=(v)=>typeof v==='string';
const isNumber=(v)=>typeof v==='number'&&!Number.isNaN(v);
const formatBytes=(n)=>{if(n==null)return '';const u=['B','KB','MB','GB','TB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++;}return n.toFixed(2)+' '+u[i];};
const formatCookieHeader=(c)=>{if(!c)return '';if(Array.isArray(c))return c.map(x=>typeof x==='string'?x:(x.name+'='+x.value)).join('; ');if(typeof c==='string')return c;return Object.entries(c).map(([k,v])=>k+'='+v).join('; ');};
const saveBase64ToFile=()=>{throw new Error('utils.saveBase64ToFile is not available in the embedded runtime');};
const htmlToMarkdown=(html)=>{if(!html||typeof html!=='string')return '';return html.replace(/<\\s*br\\s*\\/?>/gi,'\\n').replace(/<\\s*\\/p\\s*>/gi,'\\n\\n').replace(/<a [^>]*?href=["']([^"']+)["'][^>]*>(.*?)<\\/a>/gi,'[$2]($1)').replace(/<[^>]+>/g,'').replace(/&nbsp;/g,' ').replace(/&amp;/g,'&').replace(/\\n{3,}/g,'\\n\\n').trim();};
const throwIfLoginWall=(text,hint)=>{const t=(text||'').toString().toLowerCase();if(t.includes('login')||t.includes('sign in')||t.includes('登录'))throw new Error('login wall detected'+(hint?': '+hint:''));};
const BROWSER_JSON_SNIFF_FN='() => null';
export {delay,sleep,range,chunk,unique,compact,last,first,noop,isRecord,isString,isNumber,formatBytes,formatCookieHeader,saveBase64ToFile,htmlToMarkdown,throwIfLoginWall,BROWSER_JSON_SNIFF_FN};
`;
const LOGGER = `
const _m=(l)=>(...a)=>{try{console[l]&&console[l](...a);}catch{}};
const logger={debug:_m('debug'),info:_m('info'),warn:_m('warn'),error:_m('error'),log:_m('log')};
const getLogger=()=>logger;
const log=_m('log'); const debug=_m('debug'); const info=_m('info'); const warn=_m('warn'); const error=_m('error');
export {logger,getLogger,log,debug,info,warn,error}; export default logger;
`;
const STUB = `
function _u(n){return ()=>{throw new Error(n+' not available in embedded runtime');};}
class CDPBridge { constructor(){throw new Error('CDPBridge not available in embedded runtime');}}
class Page { constructor(){throw new Error('Page not available in embedded runtime');}}
export const launch=_u('launch'); export const launchProcess=_u('launchProcess'); export const spawn=_u('spawn');
export const resolveElectronEndpoint=async()=>null;
export const downloadFile=_u('downloadFile'); export const articleDownload=_u('articleDownload'); export const mediaDownload=_u('mediaDownload');
export const downloadArticle=articleDownload; export const downloadMedia=mediaDownload;
export const httpDownload=_u('httpDownload'); export const checkYtdlp=async()=>false;
export const sanitizeFilename=(n)=>(n||'').toString().replace(/[\\/\\\\?%*:|"<>]/g,'_').slice(0,200);
export const startProgress=()=>({update:()=>{},finish:()=>{}});
export const formatBytes=(n)=>{if(n==null)return '';const u=['B','KB','MB','GB','TB'];let i=0;while(n>=1024&&i<u.length-1){n/=1024;i++;}return n.toFixed(2)+' '+u[i];};
export const formatCookieHeader=(c)=>{if(!c)return '';if(Array.isArray(c))return c.map(x=>typeof x==='string'?x:(x.name+'='+x.value)).join('; ');if(typeof c==='string')return c;return Object.entries(c).map(([k,v])=>k+'='+v).join('; ');};
export const cdp=_u('cdp'); export const page=_u('page'); export const utils={}; export { CDPBridge, Page };
export default {launch,launchProcess,spawn,resolveElectronEndpoint,downloadFile,articleDownload,mediaDownload,downloadArticle,downloadMedia,httpDownload,checkYtdlp,sanitizeFilename,startProgress,formatBytes,formatCookieHeader,cdp,page,utils,CDPBridge,Page};
`;

const SHIM = new URL('./host.mjs', import.meta.url).href;
const map = new Map([
  ['@jackwener/opencli/registry',                   SHIM],
  ['@jackwener/opencli/errors',                     SHIM],
  ['@jackwener/opencli/utils',                      inline(UTILS)],
  ['@jackwener/opencli/logger',                     inline(LOGGER)],
  ['@jackwener/opencli/launcher',                   inline(STUB)],
  ['@jackwener/opencli/download',                   inline(STUB)],
  ['@jackwener/opencli/download/article-download',  inline(STUB)],
  ['@jackwener/opencli/download/media-download',    inline(STUB)],
  ['@jackwener/opencli/download/progress',          inline(STUB)],
  ['@jackwener/opencli/browser/cdp',                inline(STUB)],
  ['@jackwener/opencli/browser/page',               inline(STUB)],
  ['@jackwener/opencli/browser/utils',              inline(STUB)],
  // Node built-ins under node:* and bare.
  ['node:path', inline(NODE_PATH)],   ['path', inline(NODE_PATH)],
  ['node:os',   inline(NODE_OS)],     ['os',   inline(NODE_OS)],
  ['node:fs',   inline(NODE_FS)],     ['fs',   inline(NODE_FS)],
  ['node:fs/promises', inline(NODE_FS)],
  ['node:child_process', inline(NODE_CHILD)], ['child_process', inline(NODE_CHILD)],
  ['node:http',  inline(NODE_HTTP)],  ['http',  inline(NODE_HTTP)],
  ['node:https', inline(NODE_HTTP)],  ['https', inline(NODE_HTTP)],
  // crypto: passthrough to real Node crypto in the PoC, so adapter
  // hashes match what the host produces. The C# host implements the
  // same algorithms via System.Security.Cryptography.
  // For the bench PoC we let `node:crypto` resolve to the real Node
  // module (the C# host implements the same surface from
  // System.Security.Cryptography). The inline polyfill triggered TDZ
  // ('Cannot access nh before initialization') because re-importing
  // 'node:crypto' from this loader is circular.
]);

export function resolve(specifier, context, nextResolve) {
  if (map.has(specifier)) return { url: map.get(specifier), shortCircuit: true };
  return nextResolve(specifier, context);
}
