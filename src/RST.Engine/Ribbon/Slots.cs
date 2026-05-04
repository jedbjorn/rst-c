// Slots.cs — pool of IExternalCommand classes keyed to SlotRegistry indices.
//
// Generated boilerplate. To add capacity: bump SlotRegistry.Capacity and
// regenerate this file (one Slot## per index).
//
// Why the [Transaction]/[Regeneration] attributes are repeated on every
// concrete class (rather than only on SlotCommandBase): Revit's command
// loader looks them up with `inherit: false`. Attributes on the base
// class are NOT picked up by the runtime check — Revit pops a
// "No Transaction Attribute" dialog and refuses to run the command.
// This is a long-known Revit API quirk, not a .NET reflection mistake.

using Autodesk.Revit.Attributes;

namespace RST.Engine.Ribbon.Slots;

[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot000 : SlotCommandBase { protected override int Index => 0; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot001 : SlotCommandBase { protected override int Index => 1; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot002 : SlotCommandBase { protected override int Index => 2; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot003 : SlotCommandBase { protected override int Index => 3; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot004 : SlotCommandBase { protected override int Index => 4; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot005 : SlotCommandBase { protected override int Index => 5; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot006 : SlotCommandBase { protected override int Index => 6; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot007 : SlotCommandBase { protected override int Index => 7; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot008 : SlotCommandBase { protected override int Index => 8; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot009 : SlotCommandBase { protected override int Index => 9; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot010 : SlotCommandBase { protected override int Index => 10; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot011 : SlotCommandBase { protected override int Index => 11; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot012 : SlotCommandBase { protected override int Index => 12; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot013 : SlotCommandBase { protected override int Index => 13; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot014 : SlotCommandBase { protected override int Index => 14; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot015 : SlotCommandBase { protected override int Index => 15; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot016 : SlotCommandBase { protected override int Index => 16; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot017 : SlotCommandBase { protected override int Index => 17; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot018 : SlotCommandBase { protected override int Index => 18; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot019 : SlotCommandBase { protected override int Index => 19; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot020 : SlotCommandBase { protected override int Index => 20; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot021 : SlotCommandBase { protected override int Index => 21; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot022 : SlotCommandBase { protected override int Index => 22; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot023 : SlotCommandBase { protected override int Index => 23; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot024 : SlotCommandBase { protected override int Index => 24; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot025 : SlotCommandBase { protected override int Index => 25; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot026 : SlotCommandBase { protected override int Index => 26; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot027 : SlotCommandBase { protected override int Index => 27; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot028 : SlotCommandBase { protected override int Index => 28; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot029 : SlotCommandBase { protected override int Index => 29; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot030 : SlotCommandBase { protected override int Index => 30; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot031 : SlotCommandBase { protected override int Index => 31; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot032 : SlotCommandBase { protected override int Index => 32; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot033 : SlotCommandBase { protected override int Index => 33; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot034 : SlotCommandBase { protected override int Index => 34; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot035 : SlotCommandBase { protected override int Index => 35; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot036 : SlotCommandBase { protected override int Index => 36; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot037 : SlotCommandBase { protected override int Index => 37; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot038 : SlotCommandBase { protected override int Index => 38; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot039 : SlotCommandBase { protected override int Index => 39; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot040 : SlotCommandBase { protected override int Index => 40; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot041 : SlotCommandBase { protected override int Index => 41; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot042 : SlotCommandBase { protected override int Index => 42; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot043 : SlotCommandBase { protected override int Index => 43; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot044 : SlotCommandBase { protected override int Index => 44; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot045 : SlotCommandBase { protected override int Index => 45; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot046 : SlotCommandBase { protected override int Index => 46; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot047 : SlotCommandBase { protected override int Index => 47; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot048 : SlotCommandBase { protected override int Index => 48; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot049 : SlotCommandBase { protected override int Index => 49; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot050 : SlotCommandBase { protected override int Index => 50; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot051 : SlotCommandBase { protected override int Index => 51; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot052 : SlotCommandBase { protected override int Index => 52; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot053 : SlotCommandBase { protected override int Index => 53; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot054 : SlotCommandBase { protected override int Index => 54; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot055 : SlotCommandBase { protected override int Index => 55; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot056 : SlotCommandBase { protected override int Index => 56; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot057 : SlotCommandBase { protected override int Index => 57; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot058 : SlotCommandBase { protected override int Index => 58; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot059 : SlotCommandBase { protected override int Index => 59; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot060 : SlotCommandBase { protected override int Index => 60; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot061 : SlotCommandBase { protected override int Index => 61; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot062 : SlotCommandBase { protected override int Index => 62; }
[Transaction(TransactionMode.ReadOnly)] [Regeneration(RegenerationOption.Manual)]
public sealed class Slot063 : SlotCommandBase { protected override int Index => 63; }
