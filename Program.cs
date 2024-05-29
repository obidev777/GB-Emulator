using ObEngine;
using ObEngine.Graphics;
using ObEngine.Maths;
using ProjectDMG;
using ProjectDMG.Utils;
using SharpGL.Desktop;
using SharpGL.Graphics;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GBEmulator
{
    static class Program
    {
        public class EmulatorWindow : RenderWindow
        {
            MMU mmu = null;
            CPU cpu = null;
            PPU ppu = new PPU();
            TIMER timer = null;
            JOYPAD joypad = null;
            int cpuCycles = 0;
            int cyclesThisUpdate = 0;
            bool on = false;
            string title = "GBEngine";
            string room = "mario.gb";
            public Surface Canvas;
            public int Quality = 1;

            public EmulatorWindow()
            {
                Canvas = new Surface(ppu.VRAMW*Quality, ppu.VRAMH * Quality);
                var window = Window;
                MenuStrip menu = new MenuStrip();
                menu.Items.Add(new ToolStripMenuItem("Open Room", null, (s, e) => {
                    OpenFileDialog dialog = new OpenFileDialog();
                    dialog.Filter = "GB File|*.gb";
                    if (dialog.ShowDialog() == DialogResult.OK)
                    {
                        on = false;
                        Thread.Sleep(1000);
                        mmu = new MMU();
                        cpu = new CPU(mmu);
                        ppu = new PPU();
                        timer = new TIMER();
                        joypad = new JOYPAD();
                        cpuCycles = 0;
                        cyclesThisUpdate = 0;
                        room = dialog.SafeFileName;
                        mmu.loadGamePak(dialog.FileName);
                        on = true;
                    }
                }));
                window.Controls.Add(menu);


                window.KeyDown += (s, e) => {

                    joypad.handleKeyDown(e);

                };
                window.KeyPress += (s, e) => {

                    joypad.handleKeyPress(e);

                };
                window.KeyUp += (s, e) => {

                    joypad.handleKeyUp(e);

                };
            }

            public override void OnLoad()
            {
            }

            public override void ParalellOnRender()
            {
                if (on)
                {
                    Canvas.CopyFrom(ppu.VRAM,true);
                }
            }
            public override void OnRender()
            {
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit |
                             ClearBufferMask.StencilBufferBit);
                if (on)
                {
                    try
                    {
                        GL.Enable(EnableCap.Texture2D);
                        GL.ActiveTexture(TextureUnit.Texture0);
                        Texture surfaceTexture = Texture.CreateFrom(Canvas.HandleAddr, Canvas.Width, Canvas.Heigth);
                        GL.BindTexture(TextureTarget.Texture2D, surfaceTexture.ID);
                        GL.Begin(PrimitiveType.Quads);
                        GL.TexCoord2(0f, 1f); GL.Vertex2(-1f, -1f);
                        GL.TexCoord2(1f, 1f); GL.Vertex2(1f, -1f);
                        GL.TexCoord2(1f, 0f); GL.Vertex2(1f, 1f);
                        GL.TexCoord2(0f, 0f); GL.Vertex2(-1f, 1f);
                        GL.End();
                        GL.Disable((uint)EnableCap.Texture2D, 0);
                        surfaceTexture.Delete();
                    }
                    catch (Exception ex)
                    {

                    }
                }
            }

            public override void OnUpdate(RenderLoop loop)
            {
                if (on)
                {
                    while (cyclesThisUpdate < Constants.CYCLES_PER_UPDATE)
                    {
                        cpuCycles = cpu.Exe();
                        cyclesThisUpdate += cpuCycles;
                        timer.update(cpuCycles, mmu);
                        ppu.update(cpuCycles, mmu);
                        joypad.update(mmu);
                        handleInterrupts(mmu, cpu);
                    }
                    cyclesThisUpdate -= Constants.CYCLES_PER_UPDATE;
                }
                Title = $"GBEmulator - {room} - {GameTimer.Fps}";
            }
            public override void OnResize(int w, int h)
            {
                GL.Viewport(0, 0, w, h);
            }

            public static void handleInterrupts(MMU mmu, CPU cpu)
            {
                byte IE = mmu.IE;
                byte IF = mmu.IF;
                for (int i = 0; i < 5; i++)
                {
                    if ((((IE & IF) >> i) & 0x1) == 1)
                    {
                        cpu.ExecuteInterrupt(i);
                    }
                }

                cpu.UpdateIME();
            }
            public static long nanoTime()
            {
                long nano = 10000L * Stopwatch.GetTimestamp();
                nano /= TimeSpan.TicksPerMillisecond;
                nano *= 100L;
                return nano;
            }
        }
        [STAThread]
        static void Main()
        {
            using(EmulatorWindow emu = new EmulatorWindow())
            {
                emu.Run();
            }
        }
        
    }
}
