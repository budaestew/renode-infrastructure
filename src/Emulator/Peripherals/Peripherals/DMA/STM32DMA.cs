//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.DMA
{
    public sealed class STM32DMA : BasicDoubleWordPeripheral, IKnownSize, IGPIOReceiver, INumberedGPIOOutput
    {
        public STM32DMA(IMachine machine) : base(machine)
        {
            streams = Enumerable
                .Range(0, NrOfStreams)
                .Select(id => new Stream(this, id))
                .ToArray();

            Connections = Enumerable
                .Range(0, NrOfStreams)
                .ToDictionary(idx => idx, idx => streams[idx].IRQ);

            engine = new DmaEngine(machine.GetSystemBus(this));

            transferCompleteIrqStatus = new IFlagRegisterField[NrOfStreams];
            halfTransferIrqStatus = new IFlagRegisterField[NrOfStreams];

            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            foreach(var stream in streams)
            {
                stream.Reset();
            }
        }

        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= streams.Length)
            {
                this.WarningLog("Attempted to signal DMA stream {0}. Maximum value is {1}", number, streams.Length - 1);
                return;
            }
            streams[number].OnGPIO(value);
        }

        public long Size => 0x400;

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        private void DefineRegisters()
        {
            var streamRegOffset = new int[] {0, 6, 16, 22};

            var lowInterruptStatusReg = Registers.LowInterruptStatus.Define(this)
                .WithReservedBits(12, 4)
                .WithReservedBits(28, 4);
            var highInterruptStatusReg = Registers.HighInterruptStatus.Define(this)
                .WithReservedBits(12, 4)
                .WithReservedBits(28, 4);
            var lowInterruptClearReg = Registers.LowInterruptClear.Define(this)
                .WithReservedBits(12, 4)
                .WithReservedBits(28, 4);
            var highInterruptClearReg = Registers.HighInterruptClear.Define(this)
                .WithReservedBits(12, 4)
                .WithReservedBits(28, 4);

            for(var streamIdx = 0; streamIdx < NrOfStreams / 2; streamIdx++)
            {
                var lowStreamIdx = streamIdx;
                var offset = streamRegOffset[lowStreamIdx];
                var highStreamIdx = lowStreamIdx + 4;

                lowInterruptStatusReg
                    .WithTaggedFlag($"FEIF{lowStreamIdx}", offset)
                    .WithReservedBits(offset + 1, 1)
                    .WithTaggedFlag($"DMEIF{lowStreamIdx}", offset + 2)
                    .WithTaggedFlag($"TEIF{lowStreamIdx}", offset + 3)
                    .WithFlag(offset + 4, out halfTransferIrqStatus[lowStreamIdx], FieldMode.Read, name: $"HTIF{lowStreamIdx}")
                    .WithFlag(offset + 5, out transferCompleteIrqStatus[lowStreamIdx], FieldMode.Read, name: $"TCIF{lowStreamIdx}");

                highInterruptStatusReg
                    .WithTaggedFlag($"FEIF{highStreamIdx}", offset)
                    .WithReservedBits(offset + 1, 1)
                    .WithTaggedFlag($"DMEIF{highStreamIdx}", offset + 2)
                    .WithTaggedFlag($"TEIF{highStreamIdx}", offset + 3)
                    .WithFlag(offset + 4, out halfTransferIrqStatus[highStreamIdx], FieldMode.Read, name: $"HTIF{highStreamIdx}")
                    .WithFlag(offset + 5, out transferCompleteIrqStatus[highStreamIdx], FieldMode.Read, name: $"TCIF{highStreamIdx}");

                lowInterruptClearReg
                    .WithTaggedFlag($"CFEIF{lowStreamIdx}", offset)
                    .WithReservedBits(offset + 1, 1)
                    .WithTaggedFlag($"CDMEIF{lowStreamIdx}", offset + 2)
                    .WithTaggedFlag($"CTEIF{lowStreamIdx}", offset + 3)
                    .WithFlag(offset + 4, FieldMode.Set, name: $"CHTIF{lowStreamIdx}",
                        writeCallback: (_, value) => ClearIrqFlagOnCondition(halfTransferIrqStatus[lowStreamIdx], value))
                    .WithFlag(offset + 5, FieldMode.Set, name: $"CTCIF{lowStreamIdx}",
                        writeCallback: (_, value) => ClearIrqFlagOnCondition(transferCompleteIrqStatus[lowStreamIdx], value));

                highInterruptClearReg
                    .WithTaggedFlag($"CFEIF{highStreamIdx}", offset)
                    .WithReservedBits(offset + 1, 1)
                    .WithTaggedFlag($"CDMEIF{highStreamIdx}", offset + 2)
                    .WithTaggedFlag($"CTEIF{highStreamIdx}", offset + 3)
                    .WithFlag(offset + 4, FieldMode.Set, name: $"CHTIF{highStreamIdx}",
                        writeCallback: (_, value) => ClearIrqFlagOnCondition(halfTransferIrqStatus[highStreamIdx], value))
                    .WithFlag(offset + 5, FieldMode.Set, name: $"CTCIF{highStreamIdx}",
                        writeCallback: (_, value) => ClearIrqFlagOnCondition(transferCompleteIrqStatus[highStreamIdx], value));
            }
        }

        private void ClearIrqFlagOnCondition(IFlagRegisterField flag, bool condition)
        {
            if(condition)
            {
                flag.Value = false;
                UpdateInterrupts();
            }
        }

        private void UpdateInterrupts()
        {
            for(var streamId = 0; streamId < NrOfStreams; streamId++)
            {
                var stream = streams[streamId];
                var irqValue = (stream.TransferCompleteIrqEnable && transferCompleteIrqStatus[streamId].Value)
                    || (stream.HalfTransferIrqEnable && halfTransferIrqStatus[streamId].Value);
                this.NoisyLog("IRQ {0} set to {1}", streamId, irqValue);
                stream.IRQ.Set(irqValue);
            }
        }

        private readonly IFlagRegisterField[] transferCompleteIrqStatus;
        private readonly IFlagRegisterField[] halfTransferIrqStatus;

        private readonly DmaEngine engine;
        private readonly Stream[] streams;

        private const int NrOfStreams = 8;
        private const int StreamStep = 0x18;

        private class Stream
        {
            public Stream(STM32DMA parent, int id)
            {
                this.parent = parent;
                this.id = id;

                DefineRegisters();
                Reset();
            }

            public void Reset()
            {
                dataOffset = 0;
                initialNrOfData = 0;
                IRQ.Unset();
            }

            public void OnGPIO(bool value)
            {
                if(!value)
                {
                    return;
                }

                if(!isEnabled.Value)
                {
                    parent.WarningLog("Attempting to perform a transfer on disabled DMA stream {0}. Ignoring request", id);
                    return;
                }

                // Only transfers from peripheral are using interrupt driven approach.
                // In other cases we can just transfer all data immediately.
                if(direction.Value == Direction.PeripheralToMemory)
                {
                    PerformTransfer();
                }
            }

            public IGPIO IRQ { get; } = new GPIO();

            public bool TransferCompleteIrqEnable => transferCompleteIrqEnable.Value;

            public bool HalfTransferIrqEnable => halfTransferIrqEnable.Value;

            private void DefineRegisters()
            {
                var streamOffset = id * StreamStep;

                (Registers.StreamConfiguration + streamOffset).Define(parent)
                    .WithFlag(0, out isEnabled, name: "EN", writeCallback: (_, value) => HandleEnable(value))
                    .WithTaggedFlag("DMEIE", 1)
                    .WithTaggedFlag("TEIE", 2)
                    .WithFlag(3, out halfTransferIrqEnable, name: "HTIE")
                    .WithFlag(4, out transferCompleteIrqEnable, name: "TCIE")
                    .WithTaggedFlag("PFCTRL", 5)
                    .WithEnumField(6, 2, out direction, name: "DIR")
                    .WithFlag(8, out circularMode, name: "CIRC")
                    .WithFlag(9, out peripheralIncrementOffset, name: "PINC")
                    .WithFlag(10, out memoryIncrementOffset, name: "MINC")
                    .WithEnumField(11, 2, out peripheralDataSize, name: "PSIZE")
                    .WithEnumField(13, 2, out memoryDataSize, name: "MSIZE")
                    .WithTaggedFlag("PINCOS", 15)
                    .WithTag("PL", 16, 2)
                    .WithTaggedFlag("DBM", 18)
                    .WithTaggedFlag("CT", 19)
                    .WithTaggedFlag("TRBUFF", 20)
                    .WithTag("PBURST", 21, 2)
                    .WithTag("MBURST", 23, 2)
                    .WithReservedBits(25, 7);

                (Registers.StreamNumberOfData + streamOffset).Define(parent)
                    .WithValueField(0, 16, out nrOfData, name: "NDT",
                        writeCallback: (_, value) => initialNrOfData = value)
                    .WithReservedBits(16, 16);

                (Registers.StreamPeripheralAddress + streamOffset).Define(parent)
                    .WithValueField(0, 32, out peripheralAddress, name: "PAR");

                (Registers.StreamMemory0Address + streamOffset).Define(parent)
                    .WithValueField(0, 32, out memory0Address, name: "M0A");

                (Registers.StreamMemory1Address + streamOffset).Define(parent)
                    .WithTag("M1A", 0, 32);

                (Registers.StreamFIFOControl + streamOffset).Define(parent)
                    .WithEnumField(0, 2, out fifoThreshold, name: "FTH")
                    .WithFlag(2, out directMode, name: "DMDIS")
                    .WithTag("FS", 3, 3)
                    .WithReservedBits(6, 1)
                    .WithTaggedFlag("FEIE", 7)
                    .WithReservedBits(8, 24);
            }

            private void HandleEnable(bool value)
            {
                if(value)
                {
                    // Enabling the stream restarts the transfer at M0A on hardware: the
                    // internal pointer is (re)loaded when EN goes 0 -> 1. Without this the
                    // offset survives a disable/re-arm, so a driver that re-initialises a
                    // circular RX ring mid-ring (write NDTR, set EN) has the DMA writing at
                    // the stale offset while it derives head from NDTR. The ring is then
                    // permanently desynchronised and the driver replays old ring content.
                    dataOffset = 0;
                }

                // Only transfers from peripheral are using interrupt driven approach.
                // In other cases we can just transfer all data immediately.
                if(value && direction.Value != Direction.PeripheralToMemory)
                {
                    PerformTransfer();
                }
            }

            private void PerformTransfer()
            {
                if(CreateRequest() is Request request)
                {
                    var dataUnitSize = direction.Value == Direction.PeripheralToMemory ?
                        PeripheralDataSizeInBytes : MemoryDataSizeInBytes;
                    var nrOfDataUnits = request.Size / dataUnitSize;

                    var remainingBefore = nrOfData.Value;
                    nrOfData.Value -= (ulong)nrOfDataUnits;
                    dataOffset += (ulong)request.Size;
                    parent.engine.IssueCopy(request);

                    // HTIF is set when half of the transfer is done, on every cycle in circular mode.
                    // A circular RX ring pumped only at TC and IDLE cannot tell an empty ring from a
                    // full wrap, so drivers rely on HT to see a continuous stream longer than the ring.
                    var half = initialNrOfData / 2;
                    if(remainingBefore > half && nrOfData.Value <= half)
                    {
                        parent.halfTransferIrqStatus[id].Value = true;
                        parent.UpdateInterrupts();
                    }

                    if(nrOfData.Value == 0)
                    {
                        parent.transferCompleteIrqStatus[id].Value = true;
                        dataOffset = 0;
                        if(circularMode.Value)
                        {
                            // Circular mode: reload NDTR with the last value written to the
                            // register and keep transferring, like the hardware does.
                            nrOfData.Value = initialNrOfData;
                        }
                        parent.UpdateInterrupts();
                    }
                }
                else
                {
                    parent.WarningLog("Error in DMA configuration detected. Ignoring transfer request.");
                }
            }

            private Request? CreateRequest()
            {
                if(nrOfData.Value == 0)
                {
                    parent.WarningLog("Attempting to create DMA request with no data to send. Ignoring operation.");
                    return null;
                }

                if(direction.Value == Direction.Reserved)
                {
                    parent.WarningLog("Attempting to create DMA request with direction set as Reserved. Ignoring operation.");
                    return null;
                }

                ulong sourceAddress;
                TransferType sourceTransferType;
                bool sourceIncrementOffset;

                ulong destinationAddress;
                TransferType destinationTransferType;
                bool destinationIncrementOffset;

                var memoryOffset = memoryIncrementOffset.Value ? dataOffset : 0;
                var peripheralOffset = peripheralIncrementOffset.Value ? dataOffset : 0;

                if(direction.Value == Direction.MemoryToPeripheral)
                {
                    sourceAddress = memory0Address.Value + memoryOffset;
                    sourceTransferType = DataSizeToTransferType(memoryDataSize.Value);
                    sourceIncrementOffset = memoryIncrementOffset.Value;
                    destinationAddress = peripheralAddress.Value + peripheralOffset;
                    destinationTransferType = DataSizeToTransferType(peripheralDataSize.Value);
                    destinationIncrementOffset = peripheralIncrementOffset.Value;
                }
                else
                {
                    // Memory to memory transfers use peripheral address as source address.
                    // Of course the same applies to peripheral to memory transfers.
                    sourceAddress = peripheralAddress.Value + peripheralOffset;
                    sourceTransferType = DataSizeToTransferType(peripheralDataSize.Value);
                    sourceIncrementOffset = peripheralIncrementOffset.Value;
                    destinationAddress = memory0Address.Value + memoryOffset;
                    destinationTransferType = DataSizeToTransferType(memoryDataSize.Value);
                    destinationIncrementOffset = memoryIncrementOffset.Value;
                }

                return new Request(
                    sourceAddress,
                    destinationAddress,
                    GetCurrentTransferSize(),
                    sourceTransferType,
                    destinationTransferType,
                    sourceIncrementOffset,
                    destinationIncrementOffset
                );
            }

            private TransferType DataSizeToTransferType(DataSize dataSize)
            {
                switch(dataSize)
                {
                case DataSize.Byte:
                    return TransferType.Byte;
                case DataSize.HalfWord:
                    return TransferType.Word;
                case DataSize.Word:
                    return TransferType.DoubleWord;
                default:
                    parent.WarningLog("Trying to cast Reserved (0x{0:X}) DataSize to TransferType. Defaulting to Byte.", (int)dataSize);
                    return TransferType.Byte;
                }
            }

            private int GetCurrentTransferSize()
            {
                switch(direction.Value)
                {
                case Direction.MemoryToMemory:
                    return (int)nrOfData.Value * MemoryDataSizeInBytes;
                case Direction.MemoryToPeripheral:
                    return (int)nrOfData.Value * PeripheralDataSizeInBytes;
                case Direction.PeripheralToMemory:
                    return directMode.Value ? FIFOThresholdInBytes : PeripheralDataSizeInBytes;
                default:
                    parent.WarningLog("Trying to get transfer size for Reserved DataSize. Defaulting to 0.");
                    return 0;
                }
            }

            private int FIFOThresholdInBytes => (((int)fifoThreshold.Value + 1) * FIFOSizeInBytes) / 4;

            private int PeripheralDataSizeInBytes => 1 << (int)peripheralDataSize.Value;

            private int MemoryDataSizeInBytes => 1 << (int)memoryDataSize.Value;

            private IFlagRegisterField isEnabled;
            private IFlagRegisterField circularMode;
            private ulong initialNrOfData;
            private IFlagRegisterField transferCompleteIrqEnable;
            private IFlagRegisterField halfTransferIrqEnable;
            private IEnumRegisterField<Direction> direction;
            private IFlagRegisterField peripheralIncrementOffset;
            private IFlagRegisterField memoryIncrementOffset;
            private IEnumRegisterField<DataSize> peripheralDataSize;
            private IEnumRegisterField<DataSize> memoryDataSize;
            private IValueRegisterField nrOfData;
            private IValueRegisterField peripheralAddress;
            private IValueRegisterField memory0Address;
            private IEnumRegisterField<FIFOThreshold> fifoThreshold;
            private IFlagRegisterField directMode;

            private ulong dataOffset;

            private readonly STM32DMA parent;
            private readonly int id;

            private const int FIFOSizeInBytes = 16;

            private enum Direction
            {
                PeripheralToMemory  = 0x0,
                MemoryToPeripheral  = 0x1,
                MemoryToMemory      = 0x2,
                Reserved            = 0x3,
            }

            private enum DataSize
            {
                Byte        = 0x0,
                HalfWord    = 0x1,
                Word        = 0x2,
                Reserved    = 0x3,
            }

            private enum FIFOThreshold
            {
                OneFourth       = 0x0,
                Half            = 0x1,
                ThreeFourths    = 0x2,
                Full            = 0x3,
            }
        }

        private enum Registers
        {
            LowInterruptStatus      = 0x0,  // DMA_LISR
            HighInterruptStatus     = 0x4,  // DMA_HISR
            LowInterruptClear       = 0x8,  // DMA_LIFCR
            HighInterruptClear      = 0xC,  // DMA_HIFCR
            StreamConfiguration     = 0x10, // DMA_SxCr
            StreamNumberOfData      = 0x14, // DMA_SxNDTR
            StreamPeripheralAddress = 0x18, // DMA_SxPAR
            StreamMemory0Address    = 0x1C, // DMA_SxM0AR
            StreamMemory1Address    = 0x20, // DMA_SxM1AR
            StreamFIFOControl       = 0x24, // DMA_SxFCR
        }
    }
}