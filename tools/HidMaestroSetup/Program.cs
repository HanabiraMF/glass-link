using System;
using HIDMaestro;
Console.WriteLine(string.Join(",", Enum.GetNames<HMKey>()));
Console.WriteLine("MODS="+string.Join(",", Enum.GetNames<HMKeyModifiers>()));
