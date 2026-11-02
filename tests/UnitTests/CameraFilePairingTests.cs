using Xunit;

namespace Dashcam.Remote.Control.UnitTests
{
    public sealed class CameraFilePairingTests
    {
        [Fact]
        public void PairsVideoFilesAcrossOneSecondAndCameraMarkers()
        {
            var front = CreateFile("VIDEO_F/2026-10-25_17_00_16_F.ts");
            var back = CreateFile("VIDEO_B/2026-10-25_17_00_17_B.ts");

            Assert.Equal(ExplorerFileItem.PairKey(front), ExplorerFileItem.PairKey(back));
            Assert.True(ExplorerFileItem.CameraMarker($"{front.Folder}/{front.Name}").IsFront);
            Assert.True(ExplorerFileItem.CameraMarker($"{back.Folder}/{back.Name}").IsBack);
            Assert.Equal("2026-10-25_17_00.ts", ExplorerFileItem.RemoveCameraMarkers(Path.GetFileName(front.Name)));
        }

        [Fact]
        public void PairsWhenCameraMarkerExistsOnlyInDirectory()
        {
            var front = CreateFile("VIDEO_F/2026-10-25_17_00_16.ts");
            var back = CreateFile("VIDEO_B/2026-10-25_17_00_17.ts");

            Assert.Equal(ExplorerFileItem.PairKey(front), ExplorerFileItem.PairKey(back));
            Assert.True(ExplorerFileItem.CameraMarker($"{front.Folder}/{front.Name}").IsFront);
            Assert.Equal("2026-10-25_17_00.ts", ExplorerFileItem.RemoveCameraMarkers(front.Name));
        }

        [Fact]
        public void DoesNotPairFilesWithoutCameraMarkers()
        {
            var first = CreateFile("MISC/2026-10-25_17_00_16.ts");
            var nextSecond = CreateFile("MISC/2026-10-25_17_00_17.ts");

            Assert.NotEqual(ExplorerFileItem.PairKey(first), ExplorerFileItem.PairKey(nextSecond));
        }

        [Fact]
        public void PairsDifferentCaptureSecondsWithinSameMinute()
        {
            var frontEarlier = CreateFile("VIDEO_F/2026-10-25_17_12_16_F.ts");
            var backEarlier = CreateFile("VIDEO_B/2026-10-25_17_12_17_B.ts");
            var frontLater = CreateFile("VIDEO_F/2026-10-25_17_12_51_F.ts");
            var backLater = CreateFile("VIDEO_B/2026-10-25_17_12_52_B.ts");

            Assert.Equal(ExplorerFileItem.PairKey(frontEarlier), ExplorerFileItem.PairKey(backEarlier));
            Assert.Equal(ExplorerFileItem.PairKey(frontLater), ExplorerFileItem.PairKey(backLater));
            Assert.Equal(ExplorerFileItem.PairKey(frontEarlier), ExplorerFileItem.PairKey(frontLater));
        }

        [Fact]
        public void PairsFrontAndRearPhotosAcrossOneSecond()
        {
            var front = CreateFile("PHOTO_F/2026-10-25_17_12_16_F.jpg");
            var back = CreateFile("PHOTO_B/2026-10-25_17_12_17_B.jpg");

            Assert.Equal(ExplorerFileItem.PairKey(front), ExplorerFileItem.PairKey(back));
        }

        [Fact]
        public void KeepsDifferentMinutesSeparate()
        {
            var first = CreateFile("VIDEO_F/2026-10-25_17_12_16_F.ts");
            var otherMinute = CreateFile("VIDEO_B/2026-10-25_17_13_17_B.ts");

            Assert.NotEqual(ExplorerFileItem.PairKey(first), ExplorerFileItem.PairKey(otherMinute));
        }

        private static DeviceFile CreateFile(string name) =>
            new("loop", $"/mnt/card/{name}", 100, 180, "1792951216");
    }
}
