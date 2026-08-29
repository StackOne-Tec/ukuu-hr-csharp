import {Composition} from 'remotion';
import {UKUUAttendanceDemo} from './UKUUAttendanceDemo';

export const RemotionRoot = () => (
  <Composition
    id="UKUUAttendanceDemo"
    component={UKUUAttendanceDemo}
    durationInFrames={1800}
    fps={30}
    width={1920}
    height={1080}
  />
);
