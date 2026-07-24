//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.SD
{
    public class STM32FSDMMC : STM32SDMMC, IKnownSize
    {
        public STM32FSDMMC(IMachine machine) : base(machine)
        {
            DMAReceive = new GPIO();
        }

        public GPIO DMAReceive { get; }

        public long Size => 0x400;

        protected override void InitializeRegisters()
        {
            base.InitializeRegisters();

            Clock
                .WithTaggedFlag("Clock enable bit (CLKEN)", 8)
                .WithTaggedFlag("Power saving configuration bit (PWRSAV)", 9)
                .WithTaggedFlag("Clock divider bypass enable bit (BYPASS)", 10)
                .WithTag("Wide bus mode enable bit (WIDBUS)", 11, 2)
                .WithTaggedFlag("SDMMC_CK dephasing selection bit (NEGEDGE)", 13)
                .WithTaggedFlag("HW Flow Control enable (HWFC_EN)", 14)
                .WithReservedBits(15, 17);
            Cmd
                .WithTaggedFlag("SD I/O suspend command (SDIOSuspend)", 11)
                .WithReservedBits(12, 20);
            DataCtrl
                .WithTaggedFlag("Data transfer mode selection 1: Stream or SDIO multibyte data transfer. (DTMODE)", 2)
                .WithFlag(3, out dmaEnabled, name: "DMA enable bit (DMAEN)")
                .WithReservedBits(12, 20);

            Registers.FifoCount.Define(this)
                .WithValueField(0, 24, FieldMode.Read, name: "Remaining number of words to be written to or read from the FIFO. (FIFOCOUNT)", valueProviderCallback: _ =>
                {
                    if(ReadDataBuffer.Count > 0) return (ulong)ReadDataBuffer.Count;
                    return WriteDataLeft;
                })
                .WithReservedBits(24, 8);
        }

        protected override void ReadCard(SDCard sdCard, uint size)
        {
            base.ReadCard(sdCard, size);
            if(!dmaEnabled.Value)
            {
                return;
            }
            /* The SDMMC keeps requesting DMA service as long as the receive FIFO holds data.
               How much the DMA drains per request is its own business: it depends on the
               stream's FIFO/burst configuration, not on any fixed size here. Assuming 4 bytes
               per request used to end this loop early whenever the DMA was set up with a
               larger FIFO threshold - for a 512 byte block read with a full-FIFO threshold
               only 416 bytes were delivered, NDTR never reached zero, and the transfer
               complete interrupt never fired (ST's F4 HAL SD driver then timed out).
               Request service until the FIFO is empty instead. */
            while(ReadDataBuffer.Count > 0)
            {
                var bytesLeftBefore = ReadDataBuffer.Count;
                DMAReceive.Blink();
                if(ReadDataBuffer.Count == bytesLeftBefore)
                {
                    /* The DMA is not consuming (stream disabled, or NDTR already exhausted).
                       Give up rather than spin forever. */
                    this.WarningLog("DMA did not drain the receive FIFO, {0} bytes left", bytesLeftBefore);
                    break;
                }
            }
        }

        protected override int ClkDivWidth { get => Stm32FClkDivWidth; }

        protected override int CommandFieldsOffset { get => Stm32FCommandFieldsOffset; }

        private IFlagRegisterField dmaEnabled;

        private const int Stm32FClkDivWidth = 8;
        private const int Stm32FCommandFieldsOffset = 6;

        private enum Registers
        {
            FifoCount = 0x48,
        }
    }
}
