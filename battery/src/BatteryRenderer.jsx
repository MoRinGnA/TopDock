import { useEffect, useRef, useState } from 'react';
import { MetalFx, MetalBadge, isMetalFxSupported } from 'metal-fx';

/**
 * 배터리 렌더러 (metal-fx 2.0.11)
 *
 * 배터리 상태는 퍼센트가 아니라 3분류로 표현한다:
 *   - charging : 충전 중
 *   - normal    : 일반 상태
 *   - low       : 배터리 부족
 *
 * 이렇게 하면 퍼센트 기반 게이지/빈도를 매번 계산하지 않고,
 * 색상/배지/상태 텍스트만 바꿀 수 있어서 셰이더 부담을 줄일 수 있다.
 *
 * props: batteryLevel | paused
 *
 * 배터리 표시를 활용/노출 되도록 reactjsx로 제상대 구현하면서 충분히
 * 쉽게 만들기 위해 제상대 데모를 제공한다.
 */
export function BatteryRenderer({ batteryLevel = 'normal', paused = false }) {
  const ringRef = useRef(null);
  const [theme, setTheme] = useState('dark');

  // metal-fx는 어두운 배경에서 가장 잘 보이므로 dark로 고정
  useEffect(() => {
    setTheme('dark');
  }, []);

  // metal-fx 미지원 환경이면 단순 fallback만 보여준다 (셰이더 부담 0)
  if (!isMetalFxSupported()) {
    return (
      <div
        style={{
          width: '100%',
          height: '100%',
          borderRadius: '19px',
          display: 'flex',
          alignItems: 'center',
          justifyContent: 'center',
          gap: '8px',
          flexDirection: 'column',
          color: '#fff',
          font: 'Segoe UI Variable Display, Segoe UI, -apple-system',
          background: '#0C0C0E',
        }}
      >
        {batteryLevel === 'charging' ? '충전 중' : batteryLevel === 'low' ? '부족' : '일반'}
      </div>
    );
  }

  const isCharging = batteryLevel === 'charging';
  const isLow = batteryLevel === 'low';

  // 스타일 토큰: 3분류만 사용
  const batteryColor = isCharging ? '#22c55e' : isLow ? '#ef4444' : '#FFFFFF';
  const batteryBg = isCharging
    ? 'rgba(34,197,94,0.22)'
    : isLow
      ? 'rgba(239,68,68,0.22)'
      : 'rgba(255,255,255,0.12)';

  const label = isCharging ? '충전 중' : isLow ? '부족' : '일반';
  const icon = isCharging ? '⚡' : isLow ? '⚠' : '●';

  return (
    <MetalFx
      ref={ringRef}
      preset="chromatic"
      variant="circle"
      strength={1}
      theme={theme}
      innerShadow={true}
      glowGain={0.9}
      disabledGlow={false}
      paused={paused}
    >
      <div
        style={{
          width: '100%',
          height: '100%',
          borderRadius: '19px',
          position: 'relative',
          overflow: 'hidden',
          display: 'flex',
          flexDirection: 'column',
          alignItems: 'center',
          justifyContent: 'center',
          gap: '4px',
        }}
      >
        {/* 충전/부족 색상 틴트 (일반일 땐 흰색) */}
        <div
          style={{
            width: '100%',
            height: '100%',
            borderRadius: '19px',
            background: `conic-gradient(
              ${isCharging ? 'rgba(34,197,94,0.18)' : isLow ? 'rgba(239,68,68,0.18)' : 'rgba(255,255,255,0.12)'} 0deg,
              ${isCharging ? 'rgba(34,197,94,0.08)' : isLow ? 'rgba(239,68,68,0.08)' : 'rgba(255,255,255,0.06)'} 90deg,
              ${isCharging ? 'rgba(34,197,94,0.18)' : isLow ? 'rgba(239,68,68,0.18)' : 'rgba(255,255,255,0.12)'} 180deg,
              ${isCharging ? 'rgba(34,197,94,0.08)' : isLow ? 'rgba(239,68,68,0.08)' : 'rgba(255,255,255,0.06)'} 270deg,
              ${isCharging ? 'rgba(34,197,94,0.18)' : isLow ? 'rgba(239,68,68,0.18)' : 'rgba(255,255,255,0.12)'} 360deg
            )`,
          }}
        />

        <div
          style={{
            position: 'relative',
            zIndex: 1,
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            gap: '6px',
          }}
        >
          {/* 상태 텍스트 */}
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              gap: '6px',
              font: '500 11px/1.2 Segoe UI Variable Display, Segoe UI, -apple-system',
              color: '#E5E5EA',
              textTransform: 'uppercase',
              letterSpacing: '0.08em',
            }}
          >
            <span style={{ color: batteryColor, fontWeight: 600 }}>{icon}</span>
            <span style={{ color: '#E5E5EA' }}>{label}</span>
          </div>
        </div>

        <div style={{ position: 'relative', zIndex: 1 }}>
          {/* MetalBadge — 충전/부족 배지 */}
          <MetalBadge
            style={{
              position: 'absolute',
              top: '6px',
              right: '8px',
              width: '18px',
              height: '18px',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              borderRadius: '9px',
              background: batteryBg,
              color: batteryColor,
            }}
          >
            {icon}
          </MetalBadge>
        </div>
      </div>
    </MetalFx>
  );
}
