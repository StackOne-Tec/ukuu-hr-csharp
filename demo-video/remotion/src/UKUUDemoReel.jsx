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

/* ═══════════════════════════════════════════════════════
   COLOR PALETTE
   ═══════════════════════════════════════════════════════ */
const C = {
  ink: '#0B0418',
  ink2: '#150A30',
  purple: '#7B2FBE',
  purpleLight: '#A78BFA',
  purpleGlow: 'rgba(123,47,190,0.35)',
  gold: '#F0C257',
  goldBright: '#FFE27B',
  mint: '#14A37F',
  mintLight: '#56D8B0',
  pink: '#E85D75',
  white: '#FDFAFF',
  muted: '#9B8FB0',
  surface: '#F6F2FA',
  surfaceStrong: '#E8DFF5',
};

const ease = Easing.bezier(0.22, 1, 0.36, 1);
const full = { width: '100%', height: '100%' };

const progress = (frame, from, length) =>
  interpolate(frame, [from, from + length], [0, 1], {
    extrapolateLeft: 'clamp',
    extrapolateRight: 'clamp',
    easing: ease,
  });

const visible = (frame, from, to) =>
  Math.min(progress(frame, from, 24), 1 - progress(frame, to - 24, 24));

const fadeIn = (frame, from, dur = 24) => progress(frame, from, dur);
const fadeOut = (frame, to, dur = 24) => 1 - progress(frame, to - dur, dur);
const sceneAlpha = (frame, from, to) => Math.min(fadeIn(frame, from), fadeOut(frame, to));

/* ═══════════════════════════════════════════════════════
   PARTICLE BACKGROUND — realistic floating particles
   ═══════════════════════════════════════════════════════ */
const ParticleBg = ({ count = 60, dark = true }) => {
  const frame = useCurrentFrame();
  const particles = React.useMemo(() => {
    const arr = [];
    for (let i = 0; i < count; i++) {
      const seed = i * 137.508;
      arr.push({
        x: ((seed * 7.3) % 1920),
        y: ((seed * 11.7) % 1080),
        size: 1 + (seed % 3),
        speed: 0.2 + (seed % 0.8),
        hue: i % 3 === 0 ? 270 : i % 3 === 1 ? 45 : 160,
        opacity: 0.15 + (seed % 0.25),
        drift: (seed % 40) - 20,
      });
    }
    return arr;
  }, [count]);

  return (
    <AbsoluteFill style={{ overflow: 'hidden', pointerEvents: 'none' }}>
      {particles.map((p, i) => {
        const t = frame * p.speed * 0.3;
        const x = p.x + Math.sin(t * 0.02 + i) * p.drift;
        const y = (p.y + t * 0.5) % 1120 - 20;
        const pulse = 0.7 + Math.sin(frame * 0.03 + i * 0.5) * 0.3;
        return (
          <div key={i} style={{
            position: 'absolute',
            left: x,
            top: y,
            width: p.size * 2,
            height: p.size * 2,
            borderRadius: '50%',
            background: `hsla(${p.hue}, 60%, 65%, ${p.opacity * pulse})`,
            boxShadow: p.size > 2 ? `0 0 ${p.size * 6}px hsla(${p.hue}, 60%, 65%, ${p.opacity * pulse * 0.3})` : 'none',
          }} />
        );
      })}
    </AbsoluteFill>
  );
};

/* ═══════════════════════════════════════════════════════
   AURORA BACKGROUND — cinematic gradient mesh
   ═══════════════════════════════════════════════════════ */
const Aurora = ({ dark = true }) => {
  const frame = useCurrentFrame();
  const t = frame * 0.015;
  const drift = Math.sin(t) * 60;
  const drift2 = Math.cos(t * 0.7) * 40;

  return (
    <AbsoluteFill style={{ overflow: 'hidden', pointerEvents: 'none' }}>
      {/* Grid */}
      <div style={{
        position: 'absolute', inset: 0,
        backgroundImage: dark
          ? 'linear-gradient(rgba(255,255,255,0.02) 1px, transparent 1px), linear-gradient(90deg, rgba(255,255,255,0.02) 1px, transparent 1px)'
          : 'linear-gradient(rgba(123,47,190,0.03) 1px, transparent 1px), linear-gradient(90deg, rgba(123,47,190,0.03) 1px, transparent 1px)',
        backgroundSize: '80px 80px',
      }} />
      {/* Primary orb */}
      <div style={{
        position: 'absolute', width: 900, height: 900, borderRadius: '50%',
        left: 500 + drift, top: -200,
        background: dark
          ? 'radial-gradient(circle, rgba(123,47,190,0.2), transparent 65%)'
          : 'radial-gradient(circle, rgba(123,47,190,0.12), transparent 65%)',
        filter: 'blur(40px)',
      }} />
      {/* Secondary orb */}
      <div style={{
        position: 'absolute', width: 700, height: 700, borderRadius: '50%',
        right: -100 - drift2, bottom: -200,
        background: dark
          ? 'radial-gradient(circle, rgba(232,93,117,0.12), transparent 65%)'
          : 'radial-gradient(circle, rgba(232,93,117,0.08), transparent 65%)',
        filter: 'blur(50px)',
      }} />
      {/* Gold accent */}
      <div style={{
        position: 'absolute', width: 500, height: 500, borderRadius: '50%',
        left: '40%', top: '30%',
        background: dark
          ? 'radial-gradient(circle, rgba(240,194,87,0.06), transparent 65%)'
          : 'radial-gradient(circle, rgba(240,194,87,0.04), transparent 65%)',
        filter: 'blur(60px)',
        transform: `translate(${drift * 0.5}px, ${drift2 * 0.5}px)`,
      }} />
    </AbsoluteFill>
  );
};

/* ═══════════════════════════════════════════════════════
   BRAND
   ═══════════════════════════════════════════════════════ */
const Brand = ({ small = false, light = true }) => {
  const size = small ? 28 : 48;
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: small ? 8 : 12 }}>
      <div style={{
        width: size, height: size * 0.72,
        borderRadius: `0 0 ${size * 0.42}px ${size * 0.42}px`,
        border: `${small ? 6 : 10}px solid ${light ? C.white : 'rgba(255,255,255,0.7)'}`,
        borderTop: 0, boxSizing: 'border-box', position: 'relative',
      }}>
        <div style={{
          width: small ? 6 : 8, height: small ? 6 : 8, borderRadius: '50%',
          background: C.gold, position: 'absolute', right: -3, top: -3,
        }} />
      </div>
      <span style={{
        fontFamily: 'Arial, sans-serif', color: light ? C.white : 'rgba(255,255,255,0.8)',
        fontWeight: 900, fontSize: small ? 18 : 30, letterSpacing: small ? 3 : 5,
      }}>UKUU</span>
    </div>
  );
};

/* ═══════════════════════════════════════════════════════
   EYEBROW TAG
   ═══════════════════════════════════════════════════════ */
const Eyebrow = ({ children, dark = false }) => (
  <div style={{
    display: 'inline-flex', alignItems: 'center', gap: 10,
    fontSize: 13, fontWeight: 800, letterSpacing: 3, textTransform: 'uppercase',
    color: dark ? C.purple : C.gold,
  }}>
    <span style={{
      width: 8, height: 8, borderRadius: '50%', background: C.mint,
      boxShadow: `0 0 0 5px ${dark ? 'rgba(20,163,127,0.1)' : 'rgba(86,216,176,0.15)'}`,
    }} />
    {children}
  </div>
);

/* ═══════════════════════════════════════════════════════
   SCENE WRAPPER — fade in/out with slide
   ═══════════════════════════════════════════════════════ */
const Scene = ({ from, to, children }) => {
  const frame = useCurrentFrame();
  const alpha = sceneAlpha(frame, from, to);
  const slideY = (1 - progress(frame, from, 30)) * 30;
  return (
    <AbsoluteFill style={{
      opacity: alpha,
      transform: `translateY(${slideY}px)`,
      pointerEvents: 'none',
    }}>
      {children}
    </AbsoluteFill>
  );
};

/* ═══════════════════════════════════════════════════════
   KPI CARD
   ═══════════════════════════════════════════════════════ */
const KPI = ({ value, label, color, icon, dark = true }) => (
  <div style={{
    width: 200, height: 120, borderRadius: 16,
    background: dark ? 'rgba(255,255,255,0.04)' : '#fff',
    border: `1px solid ${dark ? 'rgba(255,255,255,0.06)' : '#EAE4F1'}`,
    padding: '16px 18px', boxSizing: 'border-box',
    boxShadow: dark ? 'none' : '0 6px 18px rgba(50,32,75,0.06)',
  }}>
    <div style={{
      width: 32, height: 32, borderRadius: 10,
      background: `${color}18`, display: 'flex', alignItems: 'center', justifyContent: 'center',
      color, fontWeight: 900, fontSize: 14,
    }}>{icon}</div>
    <div style={{
      fontFamily: 'Arial, sans-serif', color: dark ? '#fff' : '#25163F',
      fontSize: 26, fontWeight: 900, marginTop: 6,
    }}>{value}</div>
    <div style={{
      fontFamily: 'Arial, sans-serif', color: dark ? 'rgba(255,255,255,0.4)' : '#837792',
      fontSize: 11, fontWeight: 700, marginTop: 2,
    }}>{label}</div>
  </div>
);

/* ═══════════════════════════════════════════════════════
   PHONE MOCKUP — clock-in scene
   ═══════════════════════════════════════════════════════ */
const PhoneClock = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const scan = progress(frame, 0, 90);
  const pulse = spring({ frame: frame - 60, fps, config: { damping: 12, stiffness: 80 } });
  const lineY = 280 + scan * 180;
  const isClocked = scan > 0.8;

  return (
    <div style={{
      width: 340, height: 680, borderRadius: 44, padding: 11,
      background: 'linear-gradient(145deg,#3D3450,#0D0818 18%,#0D0818)',
      boxShadow: '0 40px 80px rgba(0,0,0,0.5)',
      transform: `rotate(${interpolate(frame, [0, 60], [4, 0], { extrapolateLeft: 'clamp', extrapolateRight: 'clamp' })}deg)`,
    }}>
      <div style={{
        ...full, overflow: 'hidden', borderRadius: 34,
        background: 'linear-gradient(160deg,#0E0520,#1A0D3A 60%,#0B0820)',
        position: 'relative',
      }}>
        {/* Notch */}
        <div style={{ position: 'absolute', width: 90, height: 22, borderRadius: '0 0 14px 14px', background: '#050210', left: 125, top: 14, zIndex: 10 }} />
        {/* Brand */}
        <div style={{ position: 'absolute', left: 28, top: 50 }}><Brand small /></div>
        {/* Greeting */}
        <div style={{ position: 'absolute', top: 140, width: '100%', textAlign: 'center', color: '#fff', fontFamily: 'Arial, sans-serif', fontWeight: 800, fontSize: 15 }}>Good morning, Amara</div>
        <div style={{ position: 'absolute', top: 164, width: '100%', textAlign: 'center', color: C.muted, fontFamily: 'Arial, sans-serif', fontSize: 11 }}>Tuesday · 18 June</div>

        {/* Clock rings */}
        {[1, 0.75, 0.55].map((s, i) => (
          <div key={i} style={{
            position: 'absolute',
            width: 220 * s + pulse * 14, height: 220 * s + pulse * 14,
            borderRadius: '50%',
            border: `1.5px solid rgba(240,194,87,${0.2 - i * 0.05})`,
            left: 170 - (110 * s + pulse * 7),
            top: 310 - (110 * s + pulse * 7),
          }} />
        ))}

        {/* Center circle */}
        <div style={{
          position: 'absolute', width: 150, height: 150, borderRadius: '50%',
          left: 95, top: 235,
          background: 'radial-gradient(circle at 35% 30%, #7658B8, #2A1550 70%)',
          border: '8px solid rgba(240,194,87,0.1)',
          display: 'flex', alignItems: 'center', justifyContent: 'center',
        }}>
          <div style={{ fontSize: 56, color: C.gold, fontFamily: 'Arial, sans-serif', fontWeight: 900, transform: 'scaleX(0.8)', filter: 'drop-shadow(0 0 10px rgba(240,194,87,0.4))' }}>⚡</div>
        </div>

        {/* Scan line */}
        <div style={{
          position: 'absolute', left: 60, right: 60, top: lineY,
          height: 4, borderRadius: 99,
          background: C.goldBright,
          boxShadow: '0 0 24px 6px rgba(255,226,123,0.5)',
          opacity: scan,
        }} />

        {/* Status */}
        <div style={{ position: 'absolute', top: 470, width: '100%', textAlign: 'center', color: '#fff', fontFamily: 'Arial, sans-serif', fontWeight: 800, fontSize: 16 }}>
          {isClocked ? "You're clocked in" : 'Tap to clock in'}
        </div>
        <div style={{ position: 'absolute', top: 496, width: '100%', textAlign: 'center', color: isClocked ? C.mintLight : C.muted, fontFamily: 'Arial, sans-serif', fontSize: 11, fontWeight: 700 }}>
          {isClocked ? '08:57:42 · on time' : 'Verified in seconds'}
        </div>

        {/* Button */}
        <div style={{
          position: 'absolute', left: 28, right: 28, bottom: 48, height: 50, borderRadius: 14,
          background: isClocked ? C.mint : C.gold,
          color: isClocked ? '#fff' : C.ink,
          fontFamily: 'Arial, sans-serif', fontWeight: 900, fontSize: 12, letterSpacing: 2,
          display: 'flex', alignItems: 'center', justifyContent: 'center',
          boxShadow: isClocked ? '0 4px 16px rgba(20,163,127,0.3)' : '0 4px 16px rgba(240,194,87,0.3)',
        }}>
          {isClocked ? 'CLOCKED IN  ✓' : 'CLOCK IN'}
        </div>
      </div>
    </div>
  );
};

/* ═══════════════════════════════════════════════════════
   DASHBOARD MOCKUP
   ═══════════════════════════════════════════════════════ */
const Dashboard = () => {
  const frame = useCurrentFrame();
  const present = Math.round(interpolate(frame, [0, 120], [98, 132], { extrapolateLeft: 'clamp', extrapolateRight: 'clamp' }));
  const hours = Math.round(interpolate(frame, [10, 130], [684, 936], { extrapolateLeft: 'clamp', extrapolateRight: 'clamp' }));

  const rows = [
    ['AN', 'Amara N.', 'Product', '08:57', 'On time', C.mint],
    ['TM', 'Thandi M.', 'Finance', '09:04', frame > 140 ? 'Reviewed' : '+4 min late', frame > 140 ? C.mint : C.gold],
    ['KM', 'Kito M.', 'Operations', '08:51', 'On time', C.mint],
    ['RM', 'Ruth M.', 'Customer Care', '09:00', 'On time', C.mint],
  ];

  return (
    <div style={{
      width: 1400, height: 720, borderRadius: 24,
      background: 'rgba(14,6,32,0.92)',
      border: '1px solid rgba(123,47,190,0.2)',
      boxShadow: '0 40px 100px rgba(0,0,0,0.5)',
      overflow: 'hidden', display: 'flex',
      backdropFilter: 'blur(24px)',
    }}>
      {/* Sidebar */}
      <div style={{ width: 210, background: 'rgba(255,255,255,0.02)', padding: 24, boxSizing: 'border-box', borderRight: '1px solid rgba(255,255,255,0.06)' }}>
        <Brand small />
        <div style={{ fontSize: 9, fontWeight: 800, letterSpacing: 2, color: 'rgba(255,255,255,0.25)', marginTop: 48, marginBottom: 12 }}>WORKSPACE</div>
        {['⌂  Overview', '♙  People', '◷  Attendance', '≡  Time cards', '▤  Reports'].map((item, i) => (
          <div key={item} style={{
            marginTop: 6, padding: '9px 10px', borderRadius: 10,
            background: i === 2 ? 'rgba(123,47,190,0.15)' : 'transparent',
            color: i === 2 ? '#fff' : 'rgba(255,255,255,0.4)',
            fontFamily: 'Arial, sans-serif', fontSize: 12,
            fontWeight: i === 2 ? 800 : 500,
          }}>{item}</div>
        ))}
        <div style={{ marginTop: 180, padding: 14, borderRadius: 14, background: 'rgba(255,255,255,0.04)' }}>
          <div style={{ color: '#fff', fontFamily: 'Arial, sans-serif', fontWeight: 800, fontSize: 11 }}><span style={{ color: C.mint }}>●</span> Online today</div>
          <div style={{ color: 'rgba(255,255,255,0.35)', fontFamily: 'Arial, sans-serif', fontSize: 9, marginTop: 6 }}>All systems operational</div>
        </div>
      </div>

      {/* Main */}
      <div style={{ flex: 1, padding: '36px 32px', boxSizing: 'border-box' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          <div>
            <div style={{ fontFamily: 'Arial, sans-serif', color: '#fff', fontSize: 24, fontWeight: 900 }}>Time & Attendance</div>
            <div style={{ fontFamily: 'Arial, sans-serif', color: 'rgba(255,255,255,0.4)', fontSize: 12, marginTop: 6 }}>Tuesday, 18 June · live operations</div>
          </div>
          <div style={{ background: C.purple, borderRadius: 12, padding: '10px 20px', color: '#fff', fontFamily: 'Arial, sans-serif', fontWeight: 800, fontSize: 12 }}>Clock in / out</div>
        </div>

        {/* KPIs */}
        <div style={{ display: 'flex', gap: 12, marginTop: 24 }}>
          <KPI value={present} label="present" color={C.mint} icon="✓" />
          <KPI value="04" label="late arrivals" color={C.gold} icon="◷" />
          <KPI value="06" label="on leave" color={C.purpleLight} icon="◫" />
          <KPI value={`${hours}h`} label="total hours" color="#5B8DEF" icon="↗" />
        </div>

        {/* Table */}
        <div style={{ marginTop: 20, borderRadius: 16, border: '1px solid rgba(255,255,255,0.06)', background: 'rgba(255,255,255,0.02)', overflow: 'hidden' }}>
          <div style={{ padding: '14px 20px', display: 'flex', justifyContent: 'space-between', borderBottom: '1px solid rgba(255,255,255,0.04)' }}>
            <div>
              <span style={{ fontFamily: 'Arial, sans-serif', color: '#fff', fontWeight: 900, fontSize: 14 }}>Today's attendance</span>
              <span style={{ fontFamily: 'Arial, sans-serif', color: 'rgba(255,255,255,0.35)', fontSize: 10, marginLeft: 12 }}>Shift-aware status, updated live</span>
            </div>
            <span style={{ fontFamily: 'Arial, sans-serif', color: 'rgba(255,255,255,0.35)', fontSize: 10, fontWeight: 800 }}>All employees ▾</span>
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '2.2fr 1.4fr 1fr 1.1fr', padding: '10px 20px', color: 'rgba(255,255,255,0.25)', fontFamily: 'Arial, sans-serif', fontSize: 9, fontWeight: 900, letterSpacing: 1 }}>
            <span>EMPLOYEE</span><span>SHIFT</span><span>CHECK IN</span><span>STATUS</span>
          </div>
          {rows.map((r) => (
            <div key={r[0]} style={{ display: 'grid', gridTemplateColumns: '2.2fr 1.4fr 1fr 1.1fr', padding: '10px 20px', borderTop: '1px solid rgba(255,255,255,0.03)', alignItems: 'center' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <span style={{ width: 28, height: 28, borderRadius: '50%', background: `${r[5]}18`, color: r[5], display: 'grid', placeItems: 'center', fontFamily: 'Arial, sans-serif', fontSize: 9, fontWeight: 900 }}>{r[0]}</span>
                <span>
                  <b style={{ fontFamily: 'Arial, sans-serif', color: '#fff', fontSize: 12 }}>{r[1]}</b>
                  <small style={{ fontFamily: 'Arial, sans-serif', color: 'rgba(255,255,255,0.35)', display: 'block', fontSize: 9, marginTop: 2 }}>{r[2]}</small>
                </span>
              </div>
              <span style={{ fontFamily: 'Arial, sans-serif', color: 'rgba(255,255,255,0.4)', fontSize: 11, fontWeight: 600 }}>● Day shift</span>
              <span style={{ fontFamily: 'monospace', color: '#fff', fontSize: 12, fontWeight: 700 }}>{r[3]}</span>
              <span style={{ color: r[5], background: `${r[5]}14`, padding: '5px 10px', borderRadius: 12, fontFamily: 'Arial, sans-serif', fontSize: 9, fontWeight: 900, width: 'fit-content' }}>{r[4]}</span>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
};

/* ═══════════════════════════════════════════════════════
   SYNC SCENE
   ═══════════════════════════════════════════════════════ */
const SyncScene = () => {
  const frame = useCurrentFrame();
  const dot = progress(frame, 0, 120);

  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 50, width: 1300 }}>
      {/* Device */}
      <div style={{
        width: 200, height: 270, borderRadius: 22,
        background: 'linear-gradient(145deg,#2A1D42,#0D0818)',
        border: '1px solid rgba(123,47,190,0.2)',
        boxShadow: '0 20px 50px rgba(0,0,0,0.4)',
        padding: 20, boxSizing: 'border-box', textAlign: 'center',
      }}>
        <div style={{ height: 100, borderRadius: 10, background: '#0A0614', display: 'grid', placeItems: 'center' }}>
          <div style={{ textAlign: 'center' }}>
            <b style={{ color: C.gold, fontSize: 28 }}>08:57</b>
            <small style={{ display: 'block', fontSize: 10, color: C.muted, marginTop: 6 }}>Verified entry</small>
          </div>
        </div>
        <div style={{ width: 40, height: 40, borderRadius: '50%', background: C.mint, color: '#fff', display: 'grid', placeItems: 'center', fontWeight: 900, margin: '14px auto', boxShadow: '0 0 0 5px rgba(20,163,127,0.15)' }}>✓</div>
        <div style={{ fontFamily: 'Arial, sans-serif', fontWeight: 800, fontSize: 12, color: '#fff' }}>Attendance Device</div>
        <div style={{ fontFamily: 'Arial, sans-serif', color: C.muted, fontSize: 9, marginTop: 6 }}>Hikvision / CSV / API</div>
      </div>

      {/* Pipeline */}
      <div style={{ width: 180, position: 'relative', height: 80 }}>
        <div style={{ position: 'absolute', top: 38, left: 0, right: 0, borderTop: '2px dashed rgba(123,47,190,0.25)' }} />
        <div style={{
          position: 'absolute', left: `${dot * 100}%`, top: 26, width: 24, height: 24,
          borderRadius: '50%', background: 'rgba(240,194,87,0.2)',
          display: 'grid', placeItems: 'center', transform: 'translateX(-50%)',
        }}>
          <div style={{ width: 8, height: 8, borderRadius: '50%', background: C.goldBright, boxShadow: `0 0 14px ${C.gold}` }} />
        </div>
        <div style={{ position: 'absolute', bottom: -4, width: '100%', textAlign: 'center', fontFamily: 'Arial, sans-serif', fontSize: 10, color: C.muted, fontWeight: 600 }}>A clear signal, carried forward.</div>
      </div>

      {/* Sync Panel */}
      <div style={{
        width: 480, borderRadius: 22,
        background: 'rgba(255,255,255,0.04)',
        border: '1px solid rgba(255,255,255,0.08)',
        padding: 24, boxSizing: 'border-box',
        backdropFilter: 'blur(16px)',
      }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 18 }}>
          <span style={{ fontFamily: 'Arial, sans-serif', fontWeight: 900, fontSize: 17, color: '#fff' }}>UKUU Sync</span>
          <span style={{
            color: C.mint, background: 'rgba(20,163,127,0.1)',
            border: '1px solid rgba(20,163,127,0.2)',
            fontFamily: 'Arial, sans-serif', fontSize: 9, fontWeight: 900,
            letterSpacing: 1, padding: '5px 12px', borderRadius: 100,
          }}>● LIVE</span>
        </div>
        <div style={{ padding: 14, borderRadius: 12, background: 'rgba(255,255,255,0.04)', marginBottom: 14 }}>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 12, fontWeight: 800, color: '#fff' }}>New event received</div>
          <div style={{ fontFamily: 'monospace', fontSize: 11, color: 'rgba(255,255,255,0.5)', marginTop: 6, fontWeight: 700 }}>UKU-042  ·  08:57:42</div>
        </div>
        <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 12, color: '#fff', fontWeight: 800, marginBottom: 12 }}>ShiftEngine resolves status</div>
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
          {[['●', 'Shift matched', C.mint], ['●', 'Audit ready', C.purpleLight], ['●', 'On time', C.mint], ['●', 'Synced', '#5B8DEF']].map(([dot2, label, color]) => (
            <span key={label} style={{ fontFamily: 'Arial, sans-serif', fontSize: 11, fontWeight: 700, color: 'rgba(255,255,255,0.5)' }}>
              <b style={{ color, marginRight: 6 }}>{dot2}</b>{label}
            </span>
          ))}
        </div>
      </div>
    </div>
  );
};

/* ═══════════════════════════════════════════════════════
   INSIGHTS
   ═══════════════════════════════════════════════════════ */
const Insights = () => {
  const frame = useCurrentFrame();
  const values = [42, 75, 111, 87, 57, 45, 70, 81];

  return (
    <div style={{ width: 1200, height: 640, borderRadius: 24, background: 'rgba(255,255,255,0.03)', border: '1px solid rgba(255,255,255,0.06)', padding: 36, boxSizing: 'border-box', backdropFilter: 'blur(16px)' }}>
      <div style={{ display: 'flex', justifyContent: 'space-between' }}>
        <div>
          <div style={{ fontFamily: 'Arial, sans-serif', fontWeight: 900, fontSize: 22, color: '#fff' }}>Check-ins by hour</div>
          <div style={{ fontFamily: 'Arial, sans-serif', color: 'rgba(255,255,255,0.35)', fontSize: 11, marginTop: 4 }}>Live volume across every connected source</div>
        </div>
        <div style={{ background: 'rgba(255,255,255,0.06)', borderRadius: 12, padding: '10px 18px', fontFamily: 'Arial, sans-serif', color: 'rgba(255,255,255,0.5)', fontWeight: 800, fontSize: 12 }}>18 Jun · Today</div>
      </div>

      <div style={{ display: 'flex', gap: 12, marginTop: 28 }}>
        <KPI value="132" label="present now" color={C.mint} icon="●" />
        <KPI value="936h" label="total hours" color="#5B8DEF" icon="↗" />
        <KPI value="98%" label="on-time rate" color={C.purpleLight} icon="◔" />
        <KPI value="4" label="needs review" color={C.gold} icon="!" />
      </div>

      <div style={{ display: 'flex', gap: 24, marginTop: 28 }}>
        {/* Chart */}
        <div style={{ flex: 1, height: 200, display: 'flex', alignItems: 'flex-end', gap: 10, borderBottom: '1px solid rgba(255,255,255,0.06)', padding: '0 12px' }}>
          {values.map((v, i) => {
            const h = interpolate(frame, [i * 5, 40 + i * 5], [4, v], { extrapolateLeft: 'clamp', extrapolateRight: 'clamp', easing: ease });
            return (
              <div key={i} style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 6 }}>
                <div style={{
                  width: 50, height: h, borderRadius: '10px 10px 3px 3px',
                  background: i === 2 ? C.purple : C.purpleLight,
                  opacity: 0.85,
                }} />
                <span style={{ fontFamily: 'Arial, sans-serif', color: 'rgba(255,255,255,0.3)', fontSize: 9, fontWeight: 700 }}>{String(i + 7).padStart(2, '0')}</span>
              </div>
            );
          })}
        </div>

        {/* Timeline */}
        <div style={{ width: 340, height: 200, borderRadius: 18, background: C.ink2, padding: 22, boxSizing: 'border-box' }}>
          <div style={{ fontFamily: 'Arial, sans-serif', fontWeight: 900, fontSize: 14, color: '#fff' }}>Precision, without friction.</div>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 10, color: C.muted, marginTop: 6 }}>A complete timeline for every workday.</div>
          <div style={{ height: 2, background: 'rgba(255,255,255,0.08)', margin: '28px 8px 0', position: 'relative' }}>
            {[0, 0.48, 1].map((left, i) => (
              <div key={i} style={{
                position: 'absolute', left: `${left * 100}%`, top: -6,
                width: 14, height: 14, borderRadius: '50%',
                background: i === 0 ? C.gold : i === 1 ? C.mint : C.purpleLight,
                transform: 'translateX(-50%)',
              }} />
            ))}
          </div>
          <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 14, fontFamily: 'Arial, sans-serif', fontSize: 10, fontWeight: 800, color: 'rgba(255,255,255,0.7)' }}>
            <span>08:57<br /><small style={{ color: C.muted }}>On time</small></span>
            <span>09:04<br /><small style={{ color: C.muted }}>Late flag</small></span>
            <span>09:12<br /><small style={{ color: C.muted }}>Resolved</small></span>
          </div>
          <div style={{ marginTop: 14, borderRadius: 8, background: 'rgba(123,47,190,0.2)', color: C.gold, textAlign: 'center', padding: '8px', fontFamily: 'Arial, sans-serif', fontSize: 9, fontWeight: 900, letterSpacing: 1.2 }}>AUTOMATICALLY AUDITED</div>
        </div>
      </div>
    </div>
  );
};

/* ═══════════════════════════════════════════════════════
   AUDIT SCENE
   ═══════════════════════════════════════════════════════ */
const Audit = () => {
  const frame = useCurrentFrame();
  const approved = frame > 100;

  return (
    <div style={{ width: 1100, height: 580, borderRadius: 24, background: 'rgba(255,255,255,0.03)', border: '1px solid rgba(255,255,255,0.06)', padding: 36, boxSizing: 'border-box', backdropFilter: 'blur(16px)' }}>
      <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 24, fontWeight: 900, color: '#fff' }}>Exceptions, made accountable.</div>
      <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 13, color: C.muted, marginTop: 8 }}>Review the context. Make a correction. Keep the record.</div>

      <div style={{ marginTop: 28, padding: 18, borderRadius: 14, background: 'rgba(255,255,255,0.04)', border: '1px solid rgba(255,255,255,0.06)', display: 'flex', alignItems: 'center', gap: 20 }}>
        <div style={{ width: 40, height: 40, borderRadius: '50%', background: 'rgba(232,93,117,0.12)', color: C.pink, display: 'grid', placeItems: 'center', fontFamily: 'Arial, sans-serif', fontWeight: 900, fontSize: 11 }}>TM</div>
        <div style={{ width: 200 }}>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 14, fontWeight: 900, color: '#fff' }}>Thandi Mumba</div>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 11, color: C.muted, marginTop: 4 }}>Finance · Day shift 08:00–17:00</div>
        </div>
        <span style={{ fontFamily: 'Arial, sans-serif', fontSize: 12, fontWeight: 900, color: C.gold, background: 'rgba(216,156,17,0.1)', borderRadius: 10, padding: '10px 14px' }}>09:04  +4 min</span>
        <span style={{ fontFamily: 'Arial, sans-serif', fontSize: 12, fontWeight: 800, color: approved ? C.mint : 'rgba(255,255,255,0.5)', background: approved ? 'rgba(20,163,127,0.1)' : 'rgba(255,255,255,0.06)', borderRadius: 10, padding: '10px 14px' }}>{approved ? '✓ Approved' : 'Review needed'}</span>
        <span style={{ marginLeft: 'auto', fontFamily: 'Arial, sans-serif', fontSize: 12, fontWeight: 900, color: approved ? '#fff' : 'rgba(255,255,255,0.5)', background: approved ? C.purple : 'rgba(255,255,255,0.06)', borderRadius: 12, padding: '12px 22px' }}>{approved ? 'Saved  ✓' : 'Correct'}</span>
      </div>

      <div style={{ display: 'flex', gap: 28, marginTop: 24 }}>
        <div style={{ flex: 1, borderRadius: 16, background: 'rgba(255,255,255,0.03)', border: '1px solid rgba(255,255,255,0.06)', padding: 22, boxSizing: 'border-box' }}>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 13, fontWeight: 900, color: '#fff' }}>Shift policy</div>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 11, fontWeight: 700, color: C.muted, marginTop: 10 }}>Day shift · 08:00–17:00</div>
          <div style={{ height: 1, background: 'rgba(255,255,255,0.06)', margin: '14px 0' }} />
          {[['5-minute grace period', C.mint], ['Verified clock source', C.mint], ['Reason attached', C.purpleLight]].map(([label, color]) => (
            <div key={label} style={{ fontFamily: 'Arial, sans-serif', fontSize: 11, color: 'rgba(255,255,255,0.5)', fontWeight: 700, marginTop: 10 }}>
              <b style={{ color, marginRight: 6 }}>●</b>{label}
            </div>
          ))}
        </div>
        <div style={{ flex: 1, borderRadius: 16, background: C.ink2, border: '1px solid rgba(123,47,190,0.2)', padding: 22, boxSizing: 'border-box' }}>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 13, fontWeight: 900, color: '#fff' }}>Audit trail</div>
          <div style={{ width: 2, height: 80, background: 'rgba(255,255,255,0.06)', margin: '18px 6px 0' }} />
          {[['Device timestamp preserved', C.gold], [approved ? 'Context reviewed' : 'Awaiting review', C.mint], [approved ? 'Change is fully auditable' : 'No detail is lost', C.purpleLight]].map(([label, color], i) => (
            <div key={label} style={{ position: 'relative', fontFamily: 'Arial, sans-serif', fontSize: 11, color: 'rgba(255,255,255,0.6)', fontWeight: 700, margin: '-6px 0 16px 26px' }}>
              <span style={{ position: 'absolute', left: -30, top: 0, width: 10, height: 10, borderRadius: '50%', background: color }} />
              {label}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
};

/* ═══════════════════════════════════════════════════════
   FINALE
   ═══════════════════════════════════════════════════════ */
const Finale = () => (
  <AbsoluteFill style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', flexDirection: 'column' }}>
    <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 40 }}>
      <div style={{ width: 48, height: 34, borderRadius: '0 0 22px 22px', border: '8px solid rgba(255,255,255,0.7)', borderTop: 0, boxSizing: 'border-box', position: 'relative' }}>
        <div style={{ width: 7, height: 7, borderRadius: '50%', background: C.gold, position: 'absolute', right: -3, top: -3 }} />
      </div>
      <span style={{ fontFamily: 'Arial, sans-serif', color: '#fff', fontWeight: 900, fontSize: 28, letterSpacing: 4 }}>UKUU</span>
    </div>
    <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 60, lineHeight: 1.08, fontWeight: 900, color: '#fff', textAlign: 'center', letterSpacing: '-0.03em' }}>
      Attendance that moves<br />
      <span style={{ color: C.gold }}>your business forward.</span>
    </div>
    <div style={{ marginTop: 32, borderRadius: 16, background: C.gold, padding: '14px 32px', fontFamily: 'Arial, sans-serif', fontWeight: 900, letterSpacing: 1.2, color: C.ink, fontSize: 13 }}>UKUU  ·  THE WORKDAY, MADE CLEAR</div>
    <div style={{ marginTop: 28, fontFamily: 'Arial, sans-serif', color: C.muted, fontSize: 16, fontWeight: 500 }}>Clock in. Sync. Understand. Act.</div>
    <div style={{ marginTop: 16, fontFamily: 'Arial, sans-serif', color: 'rgba(255,255,255,0.25)', fontSize: 12, fontWeight: 800, letterSpacing: 2 }}>ukuuhr.com</div>
  </AbsoluteFill>
);

/* ═══════════════════════════════════════════════════════
   MAIN COMPOSITION — 62 seconds at 30fps = 1860 frames
   ═══════════════════════════════════════════════════════ */
export const UKUUDemoReel = () => {
  const frame = useCurrentFrame();
  const heroScale = 1 + progress(frame, 0, 120) * 0.02;

  return (
    <AbsoluteFill style={{ background: C.ink, overflow: 'hidden' }}>
      {/* Audio */}
      <Audio src={staticFile('ukuu-attendance-original-score.wav')} volume={0.8} />

      {/* ── Scene 1: Hero (0–270) ── */}
      <Scene from={0} to={270}>
        <Aurora />
        <ParticleBg count={50} />
        <AbsoluteFill style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', flexDirection: 'column', transform: `scale(${heroScale})` }}>
          <Brand />
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 72, lineHeight: 1.04, fontWeight: 900, color: '#fff', marginTop: 72, textAlign: 'center', letterSpacing: '-0.03em' }}>
            Every minute.<br /><span style={{ color: C.gold }}>Accounted for.</span>
          </div>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 17, color: 'rgba(255,255,255,0.5)', marginTop: 32, fontWeight: 500, textAlign: 'center' }}>
            The workforce platform that brings clarity to clock-ins, shifts, and every working hour.
          </div>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 10, color: 'rgba(255,255,255,0.25)', fontWeight: 900, letterSpacing: 3, marginTop: 140 }}>UKUU  /  TIME & ATTENDANCE</div>
        </AbsoluteFill>
      </Scene>

      {/* ── Scene 2: Clock-In (220–510) ── */}
      <Scene from={220} to={510}>
        <Aurora />
        <ParticleBg count={40} />
        <div style={{ position: 'absolute', left: 140, top: 220 }}>
          <Eyebrow>Verified in real time</Eyebrow>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 52, lineHeight: 1.1, fontWeight: 900, color: '#fff', marginTop: 24, letterSpacing: '-0.03em' }}>
            The day starts<br /><span style={{ color: C.gold }}>with a moment of trust.</span>
          </div>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 16, color: 'rgba(255,255,255,0.5)', marginTop: 24, fontWeight: 500 }}>A simple clock-in. A reliable record.</div>
          <div style={{ marginTop: 28, display: 'flex', gap: 10 }}>
            {['Secure', 'Instant', 'Human'].map(t => (
              <span key={t} style={{ padding: '6px 14px', borderRadius: 100, background: 'rgba(255,255,255,0.05)', border: '1px solid rgba(255,255,255,0.1)', fontSize: 11, fontWeight: 700, color: 'rgba(255,255,255,0.6)' }}>● {t}</span>
            ))}
          </div>
        </div>
        <div style={{ position: 'absolute', right: 200, top: 100 }}>
          <PhoneClock />
        </div>
      </Scene>

      {/* ── Scene 3: Sync (460–730) ── */}
      <Scene from={460} to={730}>
        <Aurora dark={false} />
        <ParticleBg count={30} dark={false} />
        <div style={{ position: 'absolute', top: 120, left: 0, width: '100%', textAlign: 'center' }}>
          <Eyebrow dark>Connected by design</Eyebrow>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 50, lineHeight: 1.1, fontWeight: 900, color: C.ink, marginTop: 20, letterSpacing: '-0.03em' }}>
            One event.<br /><span style={{ color: C.purple }}>A clear picture.</span>
          </div>
          <div style={{ fontFamily: 'Arial, sans-serif', fontSize: 15, color: '#6E627B', marginTop: 16, fontWeight: 500 }}>UKUU brings every attendance signal into context.</div>
        </div>
        <div style={{ position: 'absolute', left: 160, top: 400 }}>
          <SyncScene />
        </div>
      </Scene>

      {/* ── Scene 4: Dashboard (680–1040) ── */}
      <Scene from={680} to={1040}>
        <Aurora dark={false} />
        <ParticleBg count={25} dark={false} />
        <div style={{ position: 'absolute', left: 260, top: 130 }}>
          <Dashboard />
        </div>
        <div style={{ position: 'absolute', right: 100, bottom: 80, background: C.ink2, borderRadius: 16, padding: '14px 22px', boxShadow: '0 16px 40px rgba(0,0,0,0.3)' }}>
          <div style={{ fontFamily: 'Arial, sans-serif', color: C.gold, fontSize: 9, fontWeight: 900, letterSpacing: 1.5 }}>LIVE OPERATIONS</div>
          <div style={{ fontFamily: 'Arial, sans-serif', color: '#fff', fontSize: 15, fontWeight: 900, marginTop: 6 }}>Clarity at a glance.</div>
        </div>
      </Scene>

      {/* ── Scene 5: Insights (990–1340) ── */}
      <Scene from={990} to={1340}>
        <Aurora />
        <ParticleBg count={35} />
        <div style={{ position: 'absolute', left: 360, top: 120 }}>
          <Insights />
        </div>
        <div style={{ position: 'absolute', bottom: 60, width: '100%', textAlign: 'center', fontFamily: 'Arial, sans-serif', color: '#fff', fontSize: 26, fontWeight: 900 }}>
          See the workday as it happens.
          <div style={{ fontSize: 14, color: C.muted, fontWeight: 500, marginTop: 10 }}>Spot the pattern. Support the people. Move with confidence.</div>
        </div>
      </Scene>

      {/* ── Scene 6: Audit (1290–1580) ── */}
      <Scene from={1290} to={1580}>
        <Aurora dark={false} />
        <ParticleBg count={25} dark={false} />
        <div style={{ position: 'absolute', left: 410, top: 180 }}>
          <Audit />
        </div>
        <div style={{ position: 'absolute', bottom: 60, left: 100, right: 100, borderRadius: 18, background: C.ink2, padding: 16, textAlign: 'center', fontFamily: 'Arial, sans-serif', color: '#fff', fontWeight: 800, fontSize: 15 }}>
          A trustworthy record is more than a timestamp — it is the context around it.
        </div>
      </Scene>

      {/* ── Scene 7: Finale (1530–1860) ── */}
      <Scene from={1530} to={1860}>
        <Aurora />
        <ParticleBg count={60} />
        <Finale />
      </Scene>
    </AbsoluteFill>
  );
};
