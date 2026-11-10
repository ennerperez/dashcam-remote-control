# Video object detection plan

## Existing behavior

- [x] Video playback runs through LibVLCSharp in `Dashcam.Remote.Player`.
- [x] The player can show front and rear recordings side by side.
- [x] The player window owns the video surfaces and the playback view model owns user-facing state.
- [x] The repository targets .NET 10 and Windows x64.

## Change plan

1. [x] Add trusted Tiny YOLOv2 ONNX model, ML.NET scoring pipeline, and verify package advisories.
2. [x] Sample playback frames once per second, infer off the UI thread, skip overlapping batches, and stop sampling when disabled or closing.
3. [x] Draw labels and boxes over the matching video surface, including separate front and rear results in side-by-side mode.
4. [x] Add a player control to toggle detection and report inference errors without stopping playback.
5. [x] Track detections across frames, keep boxes visible briefly between inferences, and compensate for measured movement.
6. [x] Assign stable unique colors and labels to tracked objects, including people.
7. [x] Open a supplied folder path directly in the player without showing folder selection.
8. [x] Start playback with audio unmuted at the default volume.
9. [x] Refresh box predictions every 250 ms and hide stale boxes after 750 ms while retaining tracks for association.
10. [x] Review the changed files, check whitespace, and build the player project. Automated tests were not run.

## Progress

- [x] Read the repository README, stack notes, player implementation, and ML.NET object detection documentation.
- [x] Create branch `feature/object-detection`.
- [x] Download the 63.5 MB Tiny YOLOv2 model and record its source and SHA-256.
- [x] Build the full solution successfully with 0 errors and 0 warnings to a separate output directory.
- [x] Confirm NuGet reports no vulnerable packages for Player.

## Acceptance

- Detection shows object labels and bounding boxes on the video currently being analyzed.
- Detection works for each visible camera in side-by-side mode.
- Detection includes people and draws a `Persona` tag with a persistent colored bounding box.
- Inference does not block playback or accumulate an unbounded frame queue.
- Detection stops cleanly when disabled, media changes, or the player closes.
- Build succeeds; tests run only after explicit approval.
