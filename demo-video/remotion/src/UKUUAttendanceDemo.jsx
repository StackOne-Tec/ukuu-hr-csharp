import React from 'react';
import {
  AbsoluteFill,
  Audio,
  Easing,
  interpolate,
  spring,
  staticFile,
  useCurrentFrame,
  useVideoConfig,
} from 'remotion';

const C = {
  ink: '#10091F',
  ink2: '#25163F',
  violet: '#7A5BC0',
  violetSoft: '#E8DFFF',
  gold: '#F0C257',
  mint: '#56D8B0',
  white: '#FDFBFF',
  muted: '#BDB3CA',
  line: 'rgba(255,255,255,0.12)',
};

const full = {width: '100%', height: '100%'};
const ease = Easing.bezier(0.22, 1, 0.36, 1);

const clamp = (n, low = 0, high = 1) => Math.min(high, Math.max(low, n));
const progress = (frame, from, length) => interpolate(frame, [from, from + length], [0, 1], {
  extrapolateLeft: 'clamp', extrapolateRight: 'clamp', easing: ease,
});
const visible = (frame, from, to) => Math.min(progress(frame, from, 22), 1 - progress(frame, to - 22, 22));

const Brand = ({small = false, light = true}) => {
  const size = small ? 34 : 56;
  return <div style={{display: 'flex', alignItems: 'center', gap: small ? 10 : 15}}>
    <div style={{width: size, height: size * .74, borderRadius: `0 0 ${size * .45}px ${size * .45}px`, border: `${small ? 8 : 12}px solid ${light ? C.white : C.ink2}`, borderTop: 0, boxSizing: 'border-box', position: 'relative'}}>
      <div style={{width: small ? 8 : 11, height: small ? 8 : 11, borderRadius: '50%', background: C.gold, position: 'absolute', right: -4, top: -4}} />
    </div>
    <span style={{fontFamily: 'Arial, sans-serif', color: light ? C.white : C.ink2, fontWeight: 900, fontSize: small ? 23 : 38, letterSpacing: small ? 4 : 6}}>UKUU</span>
  </div>;
};

const Background = ({light = false}) => {
  const frame = useCurrentFrame();
  const drift = Math.sin(frame / 125) * 24;
  return <AbsoluteFill style={{overflow: 'hidden', background: light ? '#F6F2FA' : C.ink}}>
    <div style={{position: 'absolute', inset: 0, backgroundImage: light
      ? 'linear-gradient(rgba(86,61,126,.055) 1px, transparent 1px), linear-gradient(90deg, rgba(86,61,126,.055) 1px, transparent 1px)'
      : 'linear-gradient(rgba(255,255,255,.045) 1px, transparent 1px), linear-gradient(90deg, rgba(255,255,255,.045) 1px, transparent 1px)', backgroundSize: '80px 80px'}} />
    <div style={{position: 'absolute', width: 1060, height: 1060, borderRadius: '50%', left: 560 + drift, top: -190, background: light ? 'radial-gradient(circle, rgba(180,147,255,.45), rgba(180,147,255,0) 67%)' : 'radial-gradient(circle, rgba(135,89,218,.50), rgba(135,89,218,0) 67%)', filter: 'blur(12px)'}} />
    <div style={{position: 'absolute', width: 830, height: 830, borderRadius: '50%', right: -200 - drift, bottom: -280, background: light ? 'radial-gradient(circle, rgba(246,203,91,.46), rgba(246,203,91,0) 69%)' : 'radial-gradient(circle, rgba(240,194,87,.23), rgba(240,194,87,0) 70%)', filter: 'blur(25px)'}} />
  </AbsoluteFill>;
};

const Scene = ({from, to, children}) => {
  const frame = useCurrentFrame();
  const opacity = visible(frame, from, to);
  const offset = (1 - progress(frame, from, 34)) * 22;
  return <AbsoluteFill style={{opacity, transform: `translateY(${offset}px)`, pointerEvents: 'none'}}>{children}</AbsoluteFill>;
};

const Eyebrow = ({children, dark = false}) => <div style={{fontFamily: 'Arial, sans-serif', color: dark ? '#7658B8' : C.gold, fontWeight: 800, fontSize: 15, letterSpacing: 3, textTransform: 'uppercase', display: 'flex', alignItems: 'center', gap: 12}}><span style={{width: 8, height: 8, borderRadius: 50, background: dark ? '#14A37F' : C.mint, boxShadow: `0 0 0 7px ${dark ? 'rgba(20,163,127,.12)' : 'rgba(86,216,176,.14)'}`}} />{children}</div>;

const PhoneClock = () => {
  const frame = useCurrentFrame();
  const {fps} = useVideoConfig();
  const scan = progress(frame, 300, 95);
  const pulse = spring({frame: frame - 300, fps, config: {damping: 12, stiffness: 100}});
  const lineY = 305 + scan * 205;
  return <div style={{width: 390, height: 770, borderRadius: 52, padding: 13, background: 'linear-gradient(145deg,#4D4161,#110C20 18%,#110C20)', boxShadow: '0 46px 80px rgba(15,5,30,.48)', transform: `rotate(${interpolate(frame, [220,420], [5,0], {extrapolateLeft:'clamp',extrapolateRight:'clamp'})}deg)`}}>
    <div style={{...full, overflow: 'hidden', borderRadius: 40, background: 'linear-gradient(150deg,#130A24,#2B1747 57%,#112038)', position: 'relative'}}>
      <div style={{position: 'absolute', width: 94, height: 24, borderRadius: 15, background: '#090510', left: 148, top: 18}} />
      <div style={{position: 'absolute', left: 36, top: 66}}><Brand small /></div>
      <div style={{position: 'absolute', top: 168, width: '100%', textAlign: 'center', color: C.white, fontFamily: 'Arial, sans-serif', fontWeight: 800, fontSize: 18}}>Good morning, Amara</div>
      <div style={{position: 'absolute', top: 198, width: '100%', textAlign: 'center', color: '#C7BBD6', fontFamily: 'Arial, sans-serif', fontSize: 12}}>Tuesday · 18 June</div>
      {[1, .78, .58].map((s, i) => <div key={i} style={{position: 'absolute', width: 260 * s + pulse * 18, height: 260 * s + pulse * 18, borderRadius: '50%', border: '1px solid rgba(242,198,85,.30)', left: 195 - (130 * s + pulse * 9), top: 340 - (130 * s + pulse * 9)}} />)}
      <div style={{position: 'absolute', width: 180, height: 180, borderRadius: '50%', left: 105, top: 250, background: 'radial-gradient(circle at 38% 28%,#7658B8,#321B56 67%)', border: '12px solid rgba(240,194,87,.16)', display: 'flex', alignItems: 'center', justifyContent: 'center'}}>
        <div style={{fontSize: 72, color: C.gold, fontFamily: 'Arial, sans-serif', fontWeight: 900, transform: 'scaleX(.82)'}}>⌁</div>
      </div>
      <div style={{position: 'absolute', left: 78, top: lineY, height: 5, width: 234, borderRadius: 99, background: '#FFE078', boxShadow: '0 0 28px 8px rgba(255,222,120,.66)', opacity: scan}} />
      <div style={{position: 'absolute', top: 500, width: '100%', textAlign: 'center', color: C.white, fontFamily: 'Arial, sans-serif', fontWeight: 800, fontSize: 19}}>{scan > .8 ? 'You’re clocked in' : 'Tap to clock in'}</div>
      <div style={{position: 'absolute', top: 530, width: '100%', textAlign: 'center', color: scan > .8 ? C.mint : '#C7BBD6', fontFamily: 'Arial, sans-serif', fontSize: 12, fontWeight: 700}}>{scan > .8 ? '08:57:42 · on time' : 'Verified in seconds'}</div>
      <div style={{position: 'absolute', left: 35, right: 35, bottom: 56, height: 58, borderRadius: 17, background: scan > .8 ? '#149A75' : C.gold, color: scan > .8 ? C.white : C.ink2, fontFamily: 'Arial, sans-serif', fontWeight: 900, fontSize: 14, letterSpacing: 1.5, display: 'flex', alignItems: 'center', justifyContent: 'center'}}>{scan > .8 ? 'CLOCKED IN  ✓' : 'CLOCK IN'}</div>
    </div>
  </div>;
};

const KPI = ({value, label, color, icon}) => <div style={{width: 226, height: 134, borderRadius: 18, background: '#fff', border: '1px solid #EAE4F1', padding: '18px 20px', boxSizing: 'border-box', boxShadow: '0 8px 22px rgba(50,32,75,.07)'}}>
  <div style={{width: 36, height: 36, borderRadius: 12, background: `${color}18`, display: 'flex', alignItems: 'center', justifyContent: 'center', color, fontWeight: 900}}>{icon}</div>
  <div style={{fontFamily: 'Arial, sans-serif', color: '#25163F', fontSize: 29, fontWeight: 900, marginTop: 7}}>{value}</div>
  <div style={{fontFamily: 'Arial, sans-serif', color: '#837792', fontSize: 12, fontWeight: 700, marginTop: 2}}>{label}</div>
</div>;

const MiniChart = () => {
  const frame = useCurrentFrame();
  const values = [42, 75, 111, 87, 57, 45, 70, 81];
  return <div style={{width: 700, height: 276, borderRadius: 20, background: '#F7F4FA', padding: '25px 28px', boxSizing: 'border-box'}}>
    <div style={{fontFamily: 'Arial, sans-serif', fontWeight: 800, fontSize: 16, color: '#322443'}}>Check-ins by hour</div>
    <div style={{fontFamily: 'Arial, sans-serif', marginTop: 6, color: '#8B8097', fontSize: 11}}>Live volume across every connected source</div>
    <div style={{height: 150, marginTop: 20, display: 'flex', gap: 30, alignItems: 'flex-end', borderBottom: '1px solid #E0D9E8', padding: '0 16px 0 12px'}}>{values.map((v, i) => {
      const h = interpolate(frame, [1015 + i * 4, 1060 + i * 4], [4, v], {extrapolateLeft:'clamp', extrapolateRight:'clamp', easing: ease});
      return <div key={i} style={{display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 9}}><div style={{width: 46, height: h, borderRadius: '12px 12px 4px 4px', background: i === 2 ? '#7658BD' : '#A58BDC'}} /><span style={{fontFamily: 'Arial, sans-serif', color: '#887D95', fontSize: 10, fontWeight: 700}}>{String(i + 7).padStart(2, '0')}</span></div>;
    })}</div>
  </div>;
};

const Dashboard = () => {
  const frame = useCurrentFrame();
  const present = Math.round(interpolate(frame, [760, 910], [98, 132], {extrapolateLeft:'clamp',extrapolateRight:'clamp'}));
  const hours = Math.round(interpolate(frame, [775, 920], [684, 936], {extrapolateLeft:'clamp',extrapolateRight:'clamp'}));
  const rows = [
    ['AN','Amara N.','Product','08:57','On time','#159B76'],
    ['TM','Thandi M.','Finance','09:04', frame > 925 ? 'Reviewed' : '+4 min late', frame > 925 ? '#159B76' : '#CF7428'],
    ['KM','Kito M.','Operations','08:51','On time','#159B76'],
    ['RM','Ruth M.','Customer care','09:00','On time','#159B76'],
  ];
  return <div style={{width: 1450, height: 770, borderRadius: 30, background: '#F9F8FB', boxShadow: '0 36px 80px rgba(15,5,30,.28)', overflow: 'hidden', display: 'flex'}}>
    <div style={{width: 238, background: '#211439', padding: 30, boxSizing: 'border-box'}}>
      <Brand small />
      <div style={{fontFamily: 'Arial, sans-serif', color: '#AFA3C3', fontSize: 10, fontWeight: 800, letterSpacing: 2, marginTop: 62}}>WORKSPACE</div>
      {['⌂  Overview','♙  People','◷  Attendance','≡  Time cards','▤  Reports'].map((item, i) => <div key={item} style={{marginTop: 17, padding: '11px 12px', borderRadius: 11, background: i === 2 ? '#3B255A' : 'transparent', color: i === 2 ? '#fff' : '#C6BCD7', fontFamily: 'Arial, sans-serif', fontSize: 13, fontWeight: i === 2 ? 800 : 500}}>{item}</div>)}
      <div style={{marginTop: 240, padding: 16, borderRadius: 16, background: '#31204B'}}><div style={{color: C.white, fontFamily: 'Arial, sans-serif', fontWeight: 800, fontSize: 12}}><span style={{color: C.gold}}>●</span>  Online today</div><div style={{color: '#BFB2D4', fontFamily: 'Arial, sans-serif', fontSize: 10, marginTop: 7}}>All systems operational</div></div>
    </div>
    <div style={{flex: 1, padding: '45px 40px', boxSizing: 'border-box'}}>
      <div style={{display: 'flex', justifyContent: 'space-between', alignItems: 'center'}}><div><div style={{fontFamily: 'Arial, sans-serif', color: C.ink2, fontSize: 29, fontWeight: 900}}>Time & Attendance</div><div style={{fontFamily: 'Arial, sans-serif', color: '#82778F', fontSize: 13, marginTop: 8}}>Tuesday, 18 June · live operations</div></div><div style={{background: C.ink2, borderRadius: 15, padding: '13px 25px', color: '#fff', fontFamily: 'Arial, sans-serif', fontWeight: 800, fontSize: 13}}>Clock in / out</div></div>
      <div style={{display: 'flex', gap: 15, marginTop: 30}}><KPI value={present} label="present" color="#159B76" icon="✓"/><KPI value="04" label="late arrivals" color="#D97922" icon="◷"/><KPI value="06" label="on leave" color="#7658B8" icon="◫"/><KPI value={`${hours}h`} label="total hours" color="#2563C9" icon="↗"/></div>
      <div style={{marginTop: 25, borderRadius: 20, border: '1px solid #E8E2EF', background: '#fff', overflow: 'hidden'}}>
        <div style={{padding: '19px 26px', display:'flex', justifyContent:'space-between', borderBottom:'1px solid #EEE9F2'}}><div><span style={{fontFamily:'Arial, sans-serif', color:C.ink2, fontWeight:900, fontSize:17}}>Today’s attendance</span><span style={{fontFamily:'Arial, sans-serif', color:'#887D95', fontSize:11, marginLeft:14}}>Shift-aware status, updated live</span></div><span style={{fontFamily:'Arial, sans-serif', color:'#645976', fontSize:11, fontWeight:800}}>All employees  ▾</span></div>
        <div style={{display:'grid', gridTemplateColumns:'2.2fr 1.4fr 1fr 1.1fr', padding:'13px 26px', color:'#A095AB', fontFamily:'Arial, sans-serif', fontSize:10, fontWeight:900, letterSpacing:1}}> <span>EMPLOYEE</span><span>SHIFT</span><span>CHECK IN</span><span>STATUS</span></div>
        {rows.map((r, i) => <div key={r[0]} style={{display:'grid', gridTemplateColumns:'2.2fr 1.4fr 1fr 1.1fr', padding:'12px 26px', borderTop:'1px solid #F0EDF3', alignItems:'center'}}><div style={{display:'flex', alignItems:'center', gap:10}}><span style={{width:30,height:30,borderRadius:'50%',background:`${r[5]}20`,color:r[5],display:'grid',placeItems:'center',fontFamily:'Arial, sans-serif',fontSize:9,fontWeight:900}}>{r[0]}</span><span><b style={{fontFamily:'Arial, sans-serif',color:'#352746',fontSize:13}}>{r[1]}</b><small style={{fontFamily:'Arial, sans-serif',color:'#958AA0',display:'block',fontSize:10,marginTop:3}}>{r[2]}</small></span></div><span style={{fontFamily:'Arial, sans-serif',color:'#5E526B',fontSize:11,fontWeight:700}}>● &nbsp;Day shift</span><span style={{fontFamily:'monospace',color:'#372A47',fontSize:12,fontWeight:700}}>{r[3]}</span><span style={{color:r[5],background:`${r[5]}16`,padding:'7px 10px',borderRadius:14,fontFamily:'Arial, sans-serif',fontSize:10,fontWeight:900,width:'fit-content'}}>{r[4]}</span></div>)}
      </div>
    </div>
  </div>;
};

const SyncScene = () => {
  const frame = useCurrentFrame();
  const dot = progress(frame, 465, 150);
  return <div style={{display: 'flex', alignItems: 'center', gap: 68, width: 1420}}>
    <div style={{width: 212, height: 285, borderRadius: 25, background: 'linear-gradient(145deg,#372B4A,#161023)', border: '2px solid #5A4970', boxShadow:'0 25px 55px rgba(15,5,30,.28)', padding: 20, boxSizing:'border-box', color:C.white}}><div style={{height:118,borderRadius:12,background:'#101020',display:'grid',placeItems:'center'}}><div style={{textAlign:'center',fontFamily:'Arial, sans-serif'}}><b style={{color:C.gold,fontSize:31}}>08:57</b><small style={{display:'block',fontSize:11,color:'#D6CEE4',marginTop:8}}>Verified entry</small></div></div><div style={{width:45,height:45,borderRadius:'50%',background:C.gold,color:C.ink2,display:'grid',placeItems:'center',fontWeight:900,margin:'18px auto'}}>✓</div><div style={{textAlign:'center',fontFamily:'Arial, sans-serif',fontWeight:800,fontSize:13}}>Attendance device</div><div style={{textAlign:'center',fontFamily:'Arial, sans-serif',color:'#AFA5BC',fontSize:10,marginTop:8}}>Hikvision / CSV / API</div></div>
    <div style={{width: 415, position:'relative', height:100}}><div style={{position:'absolute',top:49,left:0,right:0,borderTop:'3px dashed #7C6997'}}/><div style={{position:'absolute',left:`${dot*100}%`,top:35,width:30,height:30,borderRadius:'50%',background:'rgba(240,194,87,.22)',display:'grid',placeItems:'center',transform:'translateX(-50%)'}}><div style={{width:11,height:11,borderRadius:'50%',background:'#FFE27B',boxShadow:'0 0 18px #FFE27B'}}/></div><div style={{position:'absolute',bottom:-5,left:60,fontFamily:'Arial, sans-serif',fontSize:12,color:'#D6CCDF',fontWeight:700}}>A clear signal, carried forward.</div></div>
    <div style={{width:535,height:350,borderRadius:27,background:'#FBFAFD',boxShadow:'0 24px 55px rgba(15,5,30,.2)',padding:31,boxSizing:'border-box'}}><div style={{display:'flex',justifyContent:'space-between'}}><span style={{fontFamily:'Arial, sans-serif',color:C.ink2,fontWeight:900,fontSize:20}}>UKUU Sync</span><span style={{color:'#159B76',background:'#E5F7F0',fontFamily:'Arial, sans-serif',fontSize:10,fontWeight:900,letterSpacing:1,padding:'7px 12px',borderRadius:14}}>LIVE</span></div><div style={{marginTop:23,padding:18,borderRadius:14,background:'#F3EFF8',fontFamily:'Arial, sans-serif'}}><b style={{fontSize:13,color:'#4C405B'}}>New event received</b><span style={{fontFamily:'monospace',fontSize:12,display:'block',color:'#2A203A',marginTop:10,fontWeight:700}}>UKU-042  ·  08:57:42</span></div><div style={{fontFamily:'Arial, sans-serif',fontSize:13,color:'#3B2D4D',fontWeight:800,marginTop:28}}>ShiftEngine resolves status</div><div style={{marginTop:17,display:'grid',gridTemplateColumns:'1fr 1fr',gap:15}}>{[['●','Shift matched','#159B76'],['●','Audit ready','#7658B8'],['●','On time','#159B76'],['●','Synced','#2563C9']].map(([dot2, label, color])=><span key={label} style={{fontFamily:'Arial, sans-serif',fontSize:12,fontWeight:700,color:'#6B6378'}}><b style={{color,marginRight:7}}>{dot2}</b>{label}</span>)}</div></div>
  </div>;
};

const Insights = () => {
  const frame = useCurrentFrame();
  const p = progress(frame, 1110, 115);
  return <div style={{width: 1260, height: 700, borderRadius: 30, background:'#FBFAFD', boxShadow:'0 36px 80px rgba(15,5,30,.30)', padding:52, boxSizing:'border-box'}}>
    <div style={{display:'flex',justifyContent:'space-between'}}><div><div style={{fontFamily:'Arial, sans-serif',color:C.ink2,fontSize:29,fontWeight:900}}>Attendance, in real time.</div><div style={{fontFamily:'Arial, sans-serif',color:'#857A93',fontSize:14,marginTop:10}}>One view for the people, patterns and exceptions that matter.</div></div><div style={{background:'#F3EFF8',borderRadius:14,padding:'13px 23px',fontFamily:'Arial, sans-serif',color:'#554968',fontWeight:800,fontSize:13}}>18 Jun 2025  ·  Today</div></div>
    <div style={{display:'flex',gap:15,marginTop:35}}><KPI value="132" label="present now" color="#159B76" icon="●"/><KPI value="936h" label="total hours" color="#2563C9" icon="↗"/><KPI value="98%" label="on-time rate" color="#7658B8" icon="◔"/><KPI value="4" label="needs review" color="#D97922" icon="!"/></div>
    <div style={{display:'flex',gap:30,marginTop:35}}><MiniChart/><div style={{width:390,height:276,borderRadius:20,background:C.ink2,padding:29,boxSizing:'border-box',color:C.white}}><div style={{fontFamily:'Arial, sans-serif',fontWeight:900,fontSize:17}}>Precision, without friction.</div><div style={{fontFamily:'Arial, sans-serif',fontSize:12,color:'#C9BDD8',marginTop:10}}>A complete timeline for every workday.</div><div style={{height:2,background:'#6A577E',margin:'45px 10px 0',position:'relative'}}>{[0, .48, 1].map((left,i)=><div key={i} style={{position:'absolute',left:`${left*100}%`,top:-7,width:16,height:16,borderRadius:'50%',background:i===0?C.gold:i===1?C.mint:'#AB92E0',transform:'translateX(-50%)'}} />)}</div><div style={{display:'flex',justifyContent:'space-between',marginTop:16,fontFamily:'Arial, sans-serif',fontSize:11,fontWeight:800,color:'#E6DFF0'}}><span>08:57<br/><small style={{color:'#CFC4DB'}}>On time</small></span><span>09:04<br/><small style={{color:'#CFC4DB'}}>Late flag</small></span><span>09:12<br/><small style={{color:'#CFC4DB'}}>Resolved</small></span></div><div style={{marginTop:20,borderRadius:11,background:'#3A2858',color:C.gold,textAlign:'center',padding:'11px',fontFamily:'Arial, sans-serif',fontSize:10,fontWeight:900,letterSpacing:1.3}}>AUTOMATICALLY AUDITED</div></div></div>
  </div>;
};

const Audit = () => {
  const frame = useCurrentFrame();
  const approved = frame > 1450;
  return <div style={{width:1180,height:650,borderRadius:30,background:'#FAF9FC',boxShadow:'0 36px 80px rgba(15,5,30,.25)',padding:52,boxSizing:'border-box'}}>
    <div style={{fontFamily:'Arial, sans-serif',fontSize:29,fontWeight:900,color:C.ink2}}>Exceptions, made accountable.</div><div style={{fontFamily:'Arial, sans-serif',fontSize:14,color:'#857A93',marginTop:10}}>Review the context. Make a correction. Keep the record.</div>
    <div style={{marginTop:36,padding:23,borderRadius:18,background:'#fff',border:'1px solid #ECE6F1',display:'flex',alignItems:'center',gap:25}}><div style={{width:44,height:44,borderRadius:'50%',background:'#F1D9C7',color:'#914A22',display:'grid',placeItems:'center',fontFamily:'Arial, sans-serif',fontWeight:900,fontSize:12}}>TM</div><div style={{width:230}}><div style={{fontFamily:'Arial, sans-serif',fontSize:16,fontWeight:900,color:'#302240'}}>Thandi Mumba</div><div style={{fontFamily:'Arial, sans-serif',fontSize:12,color:'#8A7E96',marginTop:6}}>Finance · Day shift 08:00–17:00</div></div><span style={{fontFamily:'Arial, sans-serif',fontSize:13,fontWeight:900,color:'#C36620',background:'#FFF2E8',borderRadius:12,padding:'14px 18px'}}>09:04  +4 min</span><span style={{fontFamily:'Arial, sans-serif',fontSize:13,fontWeight:800,color:approved?'#148A6B':'#5E526B',background:approved?'#E7F7F0':'#F2EFF7',borderRadius:12,padding:'14px 18px'}}>{approved?'✓ Approved':'Review needed'}</span><span style={{marginLeft:'auto',fontFamily:'Arial, sans-serif',fontSize:13,fontWeight:900,color:approved?'#fff':'#4F435C',background:approved?C.ink2:'#E8E2EF',borderRadius:14,padding:'16px 28px'}}>{approved?'Saved  ✓':'Correct'}</span></div>
    <div style={{display:'flex',gap:40,marginTop:30}}><div style={{width:480,height:210,borderRadius:20,background:'#fff',border:'1px solid #ECE6F1',padding:28,boxSizing:'border-box'}}><div style={{fontFamily:'Arial, sans-serif',fontSize:15,fontWeight:900,color:'#342646'}}>Shift policy</div><div style={{fontFamily:'Arial, sans-serif',fontSize:13,fontWeight:700,color:'#63576F',marginTop:12}}>Day shift · 08:00–17:00</div><div style={{height:1,background:'#EDE8F1',margin:'20px 0'}}>{[['5-minute grace period','#159B76'],['Verified clock source','#159B76'],['Reason attached','#7658B8']].map(([label,color],i)=><div key={label} style={{fontFamily:'Arial, sans-serif',fontSize:12,color:'#6B6378',fontWeight:700,marginTop:i?15:0}}><b style={{color,marginRight:8}}>●</b>{label}</div>)}</div></div><div style={{flex:1,height:210,borderRadius:20,background:C.ink2,padding:28,boxSizing:'border-box'}}><div style={{fontFamily:'Arial, sans-serif',fontSize:15,fontWeight:900,color:C.white}}>Audit trail</div><div style={{height:2,background:'#6B567E',margin:'31px 0 0 14px',width:2}} />{[['Device timestamp preserved',C.gold], [approved?'Context reviewed':'Awaiting review',C.mint], [approved?'Change is fully auditable':'No detail is lost','#AB92E0']].map(([label,color],i)=><div key={label} style={{position:'relative',fontFamily:'Arial, sans-serif',fontSize:12,color:'#E7E0EE',fontWeight:700,margin:'-7px 0 21px 30px'}}><span style={{position:'absolute',left:-36,top:0,width:12,height:12,borderRadius:'50%',background:color}} />{label}</div>)}</div></div>
  </div>;
};

const Finale = () => <AbsoluteFill>
  <div style={{position:'absolute',left:844,top:145,width:72,height:52,borderRadius:'0 0 34px 34px',border:'12px solid #FDFBFF',borderTop:0,boxSizing:'border-box'}}><div style={{width:11,height:11,borderRadius:'50%',background:C.gold,position:'absolute',right:-5,top:-5}} /></div>
  <div style={{position:'absolute',top:153,left:945,color:C.white,fontFamily:'Arial, sans-serif',fontWeight:900,fontSize:38,letterSpacing:6}}>UKUU</div>
  <div style={{position:'absolute',top:310,left:0,width:'100%',textAlign:'center',fontFamily:'Arial, sans-serif',fontSize:65,lineHeight:1.1,fontWeight:900,color:C.white}}>Attendance that moves<br/><span style={{color:C.gold}}>your business forward.</span></div>
  <div style={{position:'absolute',top:555,left:700,width:520,borderRadius:20,padding:'20px 0',background:C.gold,textAlign:'center',fontFamily:'Arial, sans-serif',fontWeight:900,letterSpacing:1.3,color:C.ink2,fontSize:14}}>UKUU  ·  THE WORKDAY, MADE CLEAR</div>
  <div style={{position:'absolute',top:680,left:0,width:'100%',textAlign:'center',fontFamily:'Arial, sans-serif',color:'#D5CADF',fontSize:18,fontWeight:600}}>Clock in. Sync. Understand. Act.</div>
  <div style={{position:'absolute',top:880,left:0,width:'100%',textAlign:'center',fontFamily:'Arial, sans-serif',color:'#BEB2CB',fontSize:13,fontWeight:800,letterSpacing:2.2}}>ukuuhr.com</div>
</AbsoluteFill>;

export const UKUUAttendanceDemo = () => {
  const frame = useCurrentFrame();
  const heroScale = 1 + progress(frame, 0, 150) * .025;
  return <AbsoluteFill style={{background:C.ink, overflow:'hidden'}}>
    <Audio src={staticFile('ukuu-attendance-original-score.wav')} volume={0.84} />
    <Scene from={0} to={235}><Background /><div style={{...full, display:'flex',alignItems:'center',flexDirection:'column',paddingTop:240,boxSizing:'border-box',transform:`scale(${heroScale})`}}><Brand /><div style={{fontFamily:'Arial, sans-serif',fontSize:76,lineHeight:1.05,fontWeight:900,color:C.white,marginTop:92,textAlign:'center'}}>Every minute.<br/><span style={{color:C.gold}}>Accounted for.</span></div><div style={{fontFamily:'Arial, sans-serif',fontSize:18,color:'#D4C9DF',marginTop:38,fontWeight:600}}>Attendance intelligence for the way your people work.</div><div style={{fontFamily:'Arial, sans-serif',fontSize:11,color:'#B9ABC7',fontWeight:900,letterSpacing:3,marginTop:165}}>UKUU  /  TIME & ATTENDANCE</div></div></Scene>
    <Scene from={190} to={475}><Background /><div style={{position:'absolute',left:170,top:250}}><Eyebrow>Verified in real time</Eyebrow><div style={{fontFamily:'Arial, sans-serif',fontSize:61,lineHeight:1.08,fontWeight:900,color:C.white,marginTop:30}}>The day starts<br/><span style={{color:C.gold}}>with a moment of trust.</span></div><div style={{fontFamily:'Arial, sans-serif',fontSize:18,color:'#D3C8DE',marginTop:31,fontWeight:600}}>A simple clock-in. A reliable record.</div><div style={{marginTop:42,border:'1px solid rgba(255,255,255,.14)',background:'rgba(255,255,255,.08)',borderRadius:18,padding:'13px 19px',width:'fit-content',fontFamily:'Arial, sans-serif',color:C.white,fontWeight:800,fontSize:11,letterSpacing:1.2}}><span style={{color:C.mint}}>●</span>&nbsp; SECURE · INSTANT · HUMAN</div></div><div style={{position:'absolute',right:240,top:130}}><PhoneClock /></div></Scene>
    <Scene from={430} to={720}><Background light /><div style={{position:'absolute',top:140,left:150}}><Eyebrow dark>Connected by design</Eyebrow><div style={{fontFamily:'Arial, sans-serif',fontSize:58,lineHeight:1.1,fontWeight:900,color:C.ink2,marginTop:28}}>One event.<br/><span style={{color:C.violet}}>A clear picture.</span></div><div style={{fontFamily:'Arial, sans-serif',fontSize:17,color:'#6E627B',marginTop:24,fontWeight:600}}>UKUU brings every attendance signal into context.</div></div><div style={{position:'absolute',left:170,top:450}}><SyncScene /></div></Scene>
    <Scene from={670} to={1050}><Background light /><div style={{position:'absolute',left:235,top:145}}><Dashboard /></div><div style={{position:'absolute',right:140,bottom:110,background:C.ink2,borderRadius:18,padding:'18px 27px',boxShadow:'0 20px 40px rgba(15,5,30,.22)'}}><div style={{fontFamily:'Arial, sans-serif',color:C.gold,fontSize:10,fontWeight:900,letterSpacing:1.5}}>LIVE OPERATIONS</div><div style={{fontFamily:'Arial, sans-serif',color:C.white,fontSize:17,fontWeight:900,marginTop:8}}>Clarity at a glance.</div></div></Scene>
    <Scene from={1000} to={1370}><Background /><div style={{position:'absolute',left:330,top:130}}><Insights /></div><div style={{position:'absolute',bottom:75,width:'100%',textAlign:'center',fontFamily:'Arial, sans-serif',color:C.white,fontSize:29,fontWeight:900}}>See the workday as it happens.<div style={{fontSize:15,color:'#CFC4DA',fontWeight:600,marginTop:13}}>Spot the pattern. Support the people. Move with confidence.</div></div></Scene>
    <Scene from={1320} to={1630}><Background light /><div style={{position:'absolute',left:370,top:210}}><Audit /></div><div style={{position:'absolute',bottom:86,left:126,right:126,borderRadius:21,background:C.ink2,padding:20,textAlign:'center',fontFamily:'Arial, sans-serif',color:C.white,fontWeight:800,fontSize:17}}>A trustworthy record is more than a timestamp — it is the context around it.</div></Scene>
    <Scene from={1570} to={1800}><Background /><Finale /></Scene>
  </AbsoluteFill>;
};
