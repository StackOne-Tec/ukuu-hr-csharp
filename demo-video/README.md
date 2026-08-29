# UKUU attendance demo

`UKUU_Attendance_Demo_Remotion.mp4` is the final 60-second 1080p Remotion product demo focused on UKUU's attendance workflow:

1. Clock-in verification
2. Device / CSV / API sync
3. Live attendance dashboard
4. Shift-aware status and exceptions
5. Auditable corrections

The accompanying soundtrack is original and generated locally (`ukuu-attendance-original-score.wav`), so it does not require third-party music licensing.

The editable native Remotion project is in [`remotion/`](remotion/), including the composition, timing, transitions, and audio integration. Rebuild its master with:

```bash
cd demo-video/remotion
npm install
npm run render
```

To rebuild the deliverable:

```bash
node demo-video/render-attendance-demo.mjs
node demo-video/create-soundtrack.mjs
ffmpeg -framerate 30 -i demo-video/frames/frame-%05d.svg -i demo-video/ukuu-attendance-original-score.wav \
  -c:v libx264 -pix_fmt yuv420p -crf 17 -preset slow -c:a aac -b:a 192k -shortest demo-video/UKUU_Attendance_Demo.mp4
```
