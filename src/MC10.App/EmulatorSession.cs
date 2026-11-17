using System;
using MC10.Core;

namespace MC10.App
{
    /// <summary>
    /// Shared run state for the main screen and the debugger. The UI timer calls
    /// <see cref="Frame"/> about 60 times a second, which executes one frame's worth of cycles.
    /// </summary>
    public sealed class EmulatorSession
    {
        public Machine Machine { get; }
        public bool Running { get; private set; }
        /// <summary>1.0 = real time (approximately 0.89 MHz). Values above 1 run faster than real hardware.</summary>
        public double Speed { get; set; } = 1.0;
        public StopReason LastStop => Machine.LastStop;
        /// <summary>Speed up emulation while bulk text (paste, sample program) is being typed.</summary>
        public bool TurboTyping { get; set; } = true;
        public int TypingBoost { get; set; } = 10;

        public event Action StateChanged;
        public event Action<StopReason> Stopped;

        public EmulatorSession(Machine machine)
        {
            Machine = machine;
        }

        public long CyclesPerFrame => (long)(Machine.Config.ClockHz / 60.0 * Speed);

        public void Start()
        {
            if (Running) return;
            Running = true;
            StateChanged?.Invoke();
        }

        public void Pause()
        {
            if (!Running) return;
            Running = false;
            StateChanged?.Invoke();
        }

        public void Toggle()
        {
            if (Running) Pause(); else Start();
        }

        /// <summary>Run one display frame. Stops (and reports why) on breakpoint, watchpoint or illegal opcode.</summary>
        public void Frame()
        {
            if (!Running) return;
            // While a pasted text or sample program is being typed, run several emulated frames per tick
            // so long listings go in quickly; the firmware sees exactly the same key timing.
            int frames = TurboTyping && Machine.Keyboard.TextPending > 0 ? TypingBoost : 1;
            for (int i = 0; i < frames; i++)
            {
                Machine.Keyboard.Advance();
                StopReason r = Machine.RunCycles(Math.Max(1000, CyclesPerFrame));
                if (r != StopReason.Budget)
                {
                    Running = false;
                    Stopped?.Invoke(r);
                    StateChanged?.Invoke();
                    return;
                }
            }
        }

        /// <summary>Execute exactly one instruction (debugger single-step).</summary>
        public StopReason StepInstruction()
        {
            Pause();
            StopReason r = Machine.Step(ignoreBreakpoint: true);
            StateChanged?.Invoke();
            return r;
        }

        public StopReason StepOverInstruction()
        {
            Pause();
            StopReason r = Machine.StepOver();
            StateChanged?.Invoke();
            return r;
        }

        public StopReason StepOutInstruction()
        {
            Pause();
            StopReason r = Machine.StepOut();
            StateChanged?.Invoke();
            return r;
        }

        public void HardReset()
        {
            Pause();
            Machine.Reset();
            StateChanged?.Invoke();
        }

        public void NotifyChanged() => StateChanged?.Invoke();
    }
}
