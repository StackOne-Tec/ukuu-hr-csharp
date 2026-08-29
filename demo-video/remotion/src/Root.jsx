import {Composition} from 'remotion';
import {UKUUAttendanceDemo} from './UKUUAttendanceDemo';
import {UKUUDemoReel} from './UKUUDemoReel';

export const RemotionRoot = () => (
  <>
    <Composition
      id="UKUUAttendanceDemo"
      component={UKUUAttendanceDemo}
      durationInFrames={1800}
      fps={30}
      width={1920}
      height={1080}
    />
    <Composition
      id="UKUUDemoReel"
      component={UKUUDemoReel}
      durationInFrames={1860}
      fps={30}
      width={1920}
      height={1080}
    />
  </>
);
