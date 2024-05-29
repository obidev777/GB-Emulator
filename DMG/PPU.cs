﻿using ObEngine.Graphics;
using ObEngine.Maths;
using ObEngine.Windows;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static ProjectDMG.Utils.BitOps;

namespace ProjectDMG {
    public class PPU {

        private const int SCREEN_WIDTH = 160;
        private const int SCREEN_HEIGHT = 144;
        private const int SCREEN_VBLANK_HEIGHT = 153;
        private const int OAM_CYCLES = 80;
        private const int VRAM_CYCLES = 172;
        private const int HBLANK_CYCLES = 204;
        private const int SCANLINE_CYCLES = 456;

        private const int VBLANK_INTERRUPT = 0;
        private const int LCD_INTERRUPT = 1;

        private int[] color = new int[] { ObEngine.Maths.Color.White.ToArgb(), 
            ObEngine.Maths.Color.DarkGray.ToArgb(), 
            ObEngine.Maths.Color.DarkSaleteBlue.ToArgb(),
            ObEngine.Maths.Color.Black.ToArgb()};

        public int Quality = 2;
        private int scanlineCounter;
        private Dictionary<int, int[]> CacheColors = new Dictionary<int, int[]>();

        public int[] VRAM { get; private set; }
        public int VRAMW { get; private set; }
        public int VRAMH { get; private set; }

        public PPU(int quality=3) {
            Quality = quality;

            VRAMW = (160 * Quality);
            VRAMH = (160 * Quality);
            VRAM = new int[VRAMW * VRAMH];

            foreach(var colorData in color)
            {
                int[] cache = new int[Quality];
                for (int i = 0; i < Quality; i++)
                    cache[i] = colorData;
                CacheColors.Add(colorData, cache);
            }
        }

        public void update(int cycles, MMU mmu) {
            scanlineCounter += cycles;
            byte currentMode = (byte)(mmu.STAT & 0x3); //Current Mode Mask

            if (isLCDEnabled(mmu.LCDC)) {
                switch (currentMode) {
                    case 2: //Accessing OAM - Mode 2 (80 cycles)
                        if (scanlineCounter >= OAM_CYCLES) {
                            changeSTATMode(3, mmu);
                            scanlineCounter -= OAM_CYCLES;
                        }
                        break;
                    case 3: //Accessing VRAM - Mode 3 (172 cycles) Total M2+M3 = 252 Cycles
                        if (scanlineCounter >= VRAM_CYCLES) {
                            changeSTATMode(0, mmu);
                            drawScanLine(mmu);
                            scanlineCounter -= VRAM_CYCLES;
                        }
                        break;
                    case 0: //HBLANK - Mode 0 (204 cycles) Total M2+M3+M0 = 456 Cycles
                        if (scanlineCounter >= HBLANK_CYCLES) {
                            mmu.LY++;
                            scanlineCounter -= HBLANK_CYCLES;

                            if (mmu.LY == SCREEN_HEIGHT) { //check if we arrived Vblank
                                changeSTATMode(1, mmu);
                                mmu.requestInterrupt(VBLANK_INTERRUPT);
                            } else { //not arrived yet so return to 2
                                changeSTATMode(2, mmu);
                            }
                        }
                        break;
                    case 1: //VBLANK - Mode 1 (4560 cycles - 10 lines)
                        if (scanlineCounter >= SCANLINE_CYCLES) {
                            mmu.LY++;
                            scanlineCounter -= SCANLINE_CYCLES;

                            if (mmu.LY > SCREEN_VBLANK_HEIGHT) { //check end of VBLANK
                                changeSTATMode(2, mmu);
                                mmu.LY = 0;
                            }
                        }
                        break;
                }

                if (mmu.LY == mmu.LYC) { //handle coincidence Flag
                    mmu.STAT = bitSet(2, mmu.STAT);
                    if (isBit(6, mmu.STAT)) {
                        mmu.requestInterrupt(LCD_INTERRUPT);
                    }
                } else {
                    mmu.STAT = bitClear(2, mmu.STAT);
                }

            } else { //LCD Disabled
                scanlineCounter = 0;
                mmu.LY = 0;
                mmu.STAT = (byte)(mmu.STAT & ~0x3);
            }
        }

        private void changeSTATMode(int mode, MMU mmu) {
            byte STAT = (byte)(mmu.STAT & ~0x3);
            mmu.STAT = (byte)(STAT | mode);
            //Accessing OAM - Mode 2 (80 cycles)
            if (mode == 2 && isBit(5, STAT)) { // Bit 5 - Mode 2 OAM Interrupt         (1=Enable) (Read/Write)
                mmu.requestInterrupt(LCD_INTERRUPT);
            }

            //case 3: //Accessing VRAM - Mode 3 (172 cycles) Total M2+M3 = 252 Cycles

            //HBLANK - Mode 0 (204 cycles) Total M2+M3+M0 = 456 Cycles
            else if (mode == 0 && isBit(3, STAT)) { // Bit 3 - Mode 0 H-Blank Interrupt     (1=Enable) (Read/Write)
                mmu.requestInterrupt(LCD_INTERRUPT);
            }

            //VBLANK - Mode 1 (4560 cycles - 10 lines)
            else if (mode == 1 && isBit(4, STAT)) { // Bit 4 - Mode 1 V-Blank Interrupt     (1=Enable) (Read/Write)
                mmu.requestInterrupt(LCD_INTERRUPT);
            }

        }

        private void drawScanLine(MMU mmu) {
            byte LCDC = mmu.LCDC;
            if (isBit(0, LCDC)) { //Bit 0 - BG Display (0=Off, 1=On)
                renderBG(mmu);
            }
            if (isBit(1, LCDC)) { //Bit 1 - OBJ (Sprite) Display Enable
                renderSprites(mmu);
            }
        }

        private void renderBG(MMU mmu) {
            byte WX = (byte)(mmu.WX - 7); //WX needs -7 Offset
            byte WY = mmu.WY;
            byte LY = mmu.LY;
            byte LCDC = mmu.LCDC;
            byte SCY = mmu.SCY;
            byte SCX = mmu.SCX;
            byte BGP = mmu.BGP;
            bool isWin = isWindow(LCDC, WY, LY);

            byte y = isWin ? (byte)(LY - WY) : (byte)(LY + SCY);
            byte tileLine = (byte)((y & 7) * 2);

            ushort tileRow = (ushort)(y / 8 * 32);
            ushort tileMap = isWin ? getWindowTileMapAdress(LCDC) : getBGTileMapAdress(LCDC);

            byte hi = 0;
            byte lo = 0;

            for (int p = 0; p < SCREEN_WIDTH; p++) {
                byte x = isWin && p >= WX ? (byte)(p - WX) : (byte)(p + SCX);
                if ((p & 0x7) == 0 || ((p + SCX) & 0x7) == 0) {
                    ushort tileCol = (ushort)(x / 8);
                    ushort tileAdress = (ushort)(tileMap + tileRow + tileCol);

                    ushort tileLoc;
                    if (isSignedAdress(LCDC)) {
                        tileLoc = (ushort)(getTileDataAdress(LCDC) + mmu.readVRAM(tileAdress) * 16);
                    } else {
                        tileLoc = (ushort)(getTileDataAdress(LCDC) + ((sbyte)mmu.readVRAM(tileAdress) + 128) * 16);
                    }

                    lo = mmu.readVRAM((ushort)(tileLoc + tileLine));
                    hi = mmu.readVRAM((ushort)(tileLoc + tileLine + 1));
                }

                int colorBit = 7 - (x & 7); //inversed
                int colorId = GetColorIdBits(colorBit, lo, hi);
                int colorIdThroughtPalette = GetColorIdThroughtPalette(BGP, colorId);

                int indexX = (p - 1) * Quality;
                int indexY = (LY - 1) * Quality;
                for (int k = indexY; k < LY * Quality; k++)
                {
                    SetPixelColor(color[colorIdThroughtPalette], indexX, k);
                }
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetPixelColor(int color, int x, int y)
        {
            if (CacheColors.TryGetValue(color, out int[] cache))
            {
                int index = GetPixelIndex(x, y);
                if (index >= 0)
                    Array.Copy(cache, 0, VRAM, index, cache.Length);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetPixel(int x, int y, int colour)
        {
            if (x >= 0 && y >= 0 && x <= VRAMW && y <= VRAMH)
            {
                int index = x + (y * VRAMW);
                VRAM[index] = colour;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetPixel(int x, int y)
        {
            if (x >= 0 && y >= 0 && x <= VRAMW && y <= VRAMH)
            {
                return VRAM[GetPixelIndex(x, y)];
            }
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetPixelIndex(int x, int y) => x + (y * VRAMW);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int GetPixelIndex(int x, int y, int w) => x + (y * w);



        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetColorIdBits(int colorBit, byte l, byte h) {
            int hi = (h >> colorBit) & 0x1;
            int lo = (l >> colorBit) & 0x1;
            return (hi << 1 | lo);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetColorIdThroughtPalette(int palette, int colorId) {
            return (palette >> colorId * 2) & 0x3;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool isSignedAdress(byte LCDC) {
            //Bit 4 - BG & Window Tile Data Select   (0=8800-97FF, 1=8000-8FFF)
            return isBit(4, LCDC);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort getBGTileMapAdress(byte LCDC) {
            //Bit 3 - BG Tile Map Display Select     (0=9800-9BFF, 1=9C00-9FFF)
            return isBit(3, LCDC) ? (ushort)0x9C00 : (ushort)0x9800;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort getWindowTileMapAdress(byte LCDC) {
            //Bit 6 - Window Tile Map Display Select(0 = 9800 - 9BFF, 1 = 9C00 - 9FFF)
            return isBit(6, LCDC) ? (ushort)0x9C00 : (ushort)0x9800;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ushort getTileDataAdress(byte LCDC) {
            //Bit 4 - BG & Window Tile Data Select   (0=8800-97FF, 1=8000-8FFF)
            return isBit(4, LCDC) ? (ushort)0x8000 : (ushort)0x8800; //0x8800 signed area
        }

        private void renderSprites(MMU mmu) {
            byte LY = mmu.LY;
            byte LCDC = mmu.LCDC;
            for (int i = 0x9C; i >= 0; i -= 4) { //0x9F OAM Size, 40 Sprites x 4 bytes:
                int y = mmu.readOAM(i) - 16;    //Byte0 - Y Position //needs 16 offset
                int x = mmu.readOAM(i + 1) - 8; //Byte1 - X Position //needs 8 offset
                byte tile = mmu.readOAM(i + 2); //Byte2 - Tile/Pattern Number
                byte attr = mmu.readOAM(i + 3); //Byte3 - Attributes/Flags

                if ((LY >= y) && (LY < (y + spriteSize(LCDC)))) {
                    byte palette = isBit(4, attr) ? mmu.OBP1 : mmu.OBP0; //Bit4   Palette number  **Non CGB Mode Only** (0=OBP0, 1=OBP1)

                    int tileRow = isYFlipped(attr) ? spriteSize(LCDC) - 1 - (LY - y) : (LY - y);

                    ushort tileddress = (ushort)(0x8000 + (tile * 16) + (tileRow * 2));
                    byte lo = mmu.readVRAM(tileddress);
                    byte hi = mmu.readVRAM((ushort)(tileddress + 1));

                    for (int p = 0; p < 8; p++) {
                        int IdPos = isXFlipped(attr) ? p : 7 - p;
                        int colorId = GetColorIdBits(IdPos, lo, hi);
                        int colorIdThroughtPalette = GetColorIdThroughtPalette(palette, colorId);

                        if (((x + p)) >= 0 && ((x + p)) < SCREEN_WIDTH*Quality) {
                            if (!isTransparent(colorId) && (isAboveBG(attr) || isBGWhite(mmu.BGP, x + p, LY))) {
                                int indexX = ((p - 1)) * Quality;
                                int indexY = (LY - 1) * Quality;
                                for (int k = indexY; k < LY * Quality; k++)
                                {
                                    SetPixelColor(color[colorIdThroughtPalette], x * Quality + indexX, k);
                                }
                            }

                        }

                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool isBGWhite(byte BGP, int x, int y) {
            int id = BGP & 0x3;
            return GetPixel(x, y) == color[id];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool isAboveBG(byte attr) {
            //Bit7 OBJ-to - BG Priority(0 = OBJ Above BG, 1 = OBJ Behind BG color 1 - 3)
            return attr >> 7 == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool isLCDEnabled(byte LCDC) {
            //Bit 7 - LCD Display Enable
            return isBit(7, LCDC);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int spriteSize(byte LCDC) {
            //Bit 2 - OBJ (Sprite) Size (0=8x8, 1=8x16)
            return isBit(2, LCDC) ? 16 : 8;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool isXFlipped(int attr) {
            //Bit5   X flip(0 = Normal, 1 = Horizontally mirrored)
            return isBit(5, attr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool isYFlipped(byte attr) {
            //Bit6 Y flip(0 = Normal, 1 = Vertically mirrored)
            return isBit(6, attr);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool isTransparent(int b) {
            return b == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool isWindow(byte LCDC, byte WY, byte LY) {
            //Bit 5 - Window Display Enable (0=Off, 1=On)
            return isBit(5, LCDC) && WY <= LY;
        }
    }
}