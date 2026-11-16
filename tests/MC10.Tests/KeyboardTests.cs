using MC10.Core;
using Xunit;

namespace MC10.Tests
{
    public class KeyboardTests
    {
        static byte Rows(MatrixKeyboard k) => k.Read(0xBFFF);

        [Fact]
        public void LetterAPressesColumn1Row0()
        {
            byte strobe = 0xFF;
            var k = new MatrixKeyboard(() => strobe);
            Assert.True(k.TapChar('A'));
            k.Advance();                                   // key goes down
            strobe = 0xFD;                                 // column 1 low
            Assert.Equal(0xFE, Rows(k));                   // row 0 low
            strobe = 0xFE;                                 // a different column sees nothing
            Assert.Equal(0xFF, Rows(k));
        }

        [Fact]
        public void KeyReleasesAfterHoldFrames()
        {
            byte strobe = 0xFD;
            var k = new MatrixKeyboard(() => strobe);
            k.TapChar('A');
            for (int i = 0; i < MatrixKeyboard.HoldFrames; i++) k.Advance();
            Assert.Equal(0xFE, Rows(k));
            k.Advance();                                   // released
            Assert.Equal(0xFF, Rows(k));
        }

        [Fact]
        public void LettersNeverUseShift()
        {
            byte strobe = 0x7F;                            // column 7 low
            var k = new MatrixKeyboard(() => strobe);
            k.TapChar('a');                                // lowercase host letter -> plain key
            k.Advance();
            Assert.Equal(0xFF, k.ReadPort2());
        }

        [Fact]
        public void ShiftedSymbolUsesShiftOnPort2()
        {
            byte strobe = 0x7F;
            var k = new MatrixKeyboard(() => strobe);
            k.TapChar('!');                                // SHIFT+1
            k.Advance();
            Assert.Equal(0xFD, k.ReadPort2());             // bit 1 low = shift down
            strobe = 0xFF;
            Assert.Equal(0xFF, k.ReadPort2());
        }

        [Fact]
        public void SpaceIsColumn7Row3()
        {
            byte strobe = 0x7F;
            var k = new MatrixKeyboard(() => strobe);
            k.TapChar(' ');
            k.Advance();
            Assert.Equal(0xF7, Rows(k));
        }

        [Fact]
        public void DigitAndEnterIndices()
        {
            byte strobe = 0xFE;                            // column 0
            var k = new MatrixKeyboard(() => strobe);
            k.TapChar('0');                                // index 32 = row 4, column 0
            k.Advance();
            Assert.Equal(0xEF, Rows(k));
        }
    }
}
