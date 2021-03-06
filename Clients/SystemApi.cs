﻿using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using PluginCommon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using System.Threading;
using SystemChecker.Models;

namespace SystemChecker.Clients
{
    // https://ourcodeworld.com/articles/read/294/how-to-retrieve-basic-and-advanced-hardware-and-software-information-gpu-hard-drive-processor-os-printers-in-winforms-with-c-sharp
    class SystemApi
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private readonly string[] SizeSuffixes = { "bytes", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
        private string PluginUserDataPath { get; set; }
        private string PluginDirectory { get; set; }
        private string FilePlugin { get; set; }

        private SystemConfiguration systemConfiguration = new SystemConfiguration();
        private GameRequierements gameRequierements = new GameRequierements();

        private string SizeSuffix(Int64 value)
        {
            if (value < 0) { return "-" + SizeSuffix(-value); }
            if (value == 0) { return "0.0 bytes"; }

            int mag = (int)Math.Log(value, 1024);
            decimal adjustedSize = (decimal)value / (1L << (mag * 10));

            return string.Format("{0:n1} {1}", adjustedSize, SizeSuffixes[mag]);
        }


        public SystemApi(string PluginUserDataPath)
        {
            this.PluginUserDataPath = PluginUserDataPath;
            PluginDirectory = PluginUserDataPath + "\\SystemChecker\\";
            FilePlugin = PluginDirectory + "\\pc.json";
        }

        public SystemConfiguration GetInfo()
        {
            systemConfiguration = new SystemConfiguration();
            List<SystemDisk> Disks = GetInfoDisks();

            if (!Directory.Exists(PluginDirectory))
            {
                Directory.CreateDirectory(PluginDirectory);
            }

            if (File.Exists(FilePlugin))
            {
                try
                {
                    string JsonStringData = File.ReadAllText(FilePlugin);
                    systemConfiguration =  JsonConvert.DeserializeObject<SystemConfiguration>(JsonStringData);
                    systemConfiguration.Disks = Disks;
                    return systemConfiguration;
                }
                catch(Exception ex)
                {
                    Common.LogError(ex, "SystemChecker", $"Failed to load item from {FilePlugin}");
                }
            }

            string Name = Environment.MachineName;
            string Os = "";
            string Cpu = "";
            uint CpuMaxClockSpeed = 0;
            string GpuName = "";
            long GpuRam = 0;
            uint CurrentHorizontalResolution = 0;
            uint CurrentVerticalResolution = 0;
            long Ram = 0;


            ManagementObjectSearcher myOperativeSystemObject = new ManagementObjectSearcher("select * from Win32_OperatingSystem");
            foreach (ManagementObject obj in myOperativeSystemObject.Get())
            {
                Os = (string)obj["Caption"];
            }


            ManagementObjectSearcher myProcessorObject = new ManagementObjectSearcher("select * from Win32_Processor");
            foreach (ManagementObject obj in myProcessorObject.Get())
            {
                Cpu = (string)obj["Name"];
                CpuMaxClockSpeed = (uint)obj["MaxClockSpeed"];
            }


            ManagementObjectSearcher myVideoObject = new ManagementObjectSearcher("select * from Win32_VideoController");
            foreach (ManagementObject obj in myVideoObject.Get())
            {
                GpuName = (string)obj["Name"];
                GpuRam = (long)Convert.ToDouble(obj["AdapterRAM"]);
                CurrentHorizontalResolution = (uint)obj["CurrentHorizontalResolution"];
                CurrentVerticalResolution = (uint)obj["CurrentVerticalResolution"];
            }


            ManagementObjectSearcher myComputerSystemObject = new ManagementObjectSearcher("select * from Win32_ComputerSystem");

            foreach (ManagementObject obj in myComputerSystemObject.Get())
            {
                Ram = (long)Convert.ToDouble(obj["TotalPhysicalMemory"]);
            }


            systemConfiguration.Name = Name;
            systemConfiguration.Os = Os;
            systemConfiguration.Cpu = Cpu;
            systemConfiguration.CpuMaxClockSpeed = CpuMaxClockSpeed;
            systemConfiguration.GpuName = GpuName;
            systemConfiguration.GpuRam = GpuRam;
            systemConfiguration.CurrentHorizontalResolution = CurrentHorizontalResolution;
            systemConfiguration.CurrentVerticalResolution = CurrentVerticalResolution;
            systemConfiguration.Ram = Ram;
            systemConfiguration.RamUsage = SizeSuffix(Ram);
            systemConfiguration.Disks = Disks;


            File.WriteAllText(FilePlugin, JsonConvert.SerializeObject(systemConfiguration));
            return systemConfiguration;
        }

        private List<SystemDisk> GetInfoDisks()
        {
            List<SystemDisk> Disks = new List<SystemDisk>();
            DriveInfo[] allDrives = DriveInfo.GetDrives();
            foreach (DriveInfo d in allDrives)
            {
                Disks.Add(new SystemDisk
                {
                    Name = d.VolumeLabel,
                    Drive = d.Name,
                    FreeSpace = d.TotalFreeSpace,
                    FreeSpaceUsage = SizeSuffix(d.TotalFreeSpace)
                });
            }
            return Disks;
        }


        public GameRequierements GetGameRequierements(Game game)
        {
            gameRequierements = new GameRequierements();
            string FileGameRequierements = PluginDirectory + "\\" + game.Id.ToString() + ".json";

            string SourceName = "";
            if (game.SourceId != Guid.Parse("00000000-0000-0000-0000-000000000000"))
            {
                SourceName = "Playnite";
            }
            else
            {
                SourceName = game.Source.Name;
            }

            if (File.Exists(FileGameRequierements))
            {
                return JsonConvert.DeserializeObject<GameRequierements>(File.ReadAllText(FileGameRequierements));
            }

            SteamRequierements steamRequierements;
            switch (SourceName.ToLower())
            {
                case "steam":
                    steamRequierements = new SteamRequierements(game);
                    gameRequierements = steamRequierements.GetRequirements();
                    break;
                default:
                    SteamApi steamApi = new SteamApi(PluginUserDataPath);
                    int SteamID = steamApi.GetSteamId(game.Name);
                    if (SteamID != 0)
                    {
                        steamRequierements = new SteamRequierements(game, (uint)SteamID);
                        gameRequierements = steamRequierements.GetRequirements();
                    }
                    break;
            }

            // TODO Save only if find
            //if (gameRequierements.Minimum.Os.Count != 0 && gameRequierements.Recommanded.Os.Count != 0)
            //{
                File.WriteAllText(FileGameRequierements, JsonConvert.SerializeObject(gameRequierements));
            //}
            return gameRequierements;
        }


        public static CheckSystem CheckConfig(Requirement requirement, SystemConfiguration systemConfiguration)
        {
            if (requirement != null)
            {
                bool CheckOs = false;
                foreach (string Os in requirement.Os)
                {
                    //logger.Debug($"CheckOs - {systemConfiguration.Os} - {Os}");

                    if (systemConfiguration.Os.ToLower().IndexOf("10") > -1)
                    {
                        CheckOs = true;
                        break;
                    }

                    if (systemConfiguration.Os.ToLower().IndexOf(Os.ToLower()) > -1)
                    {
                        CheckOs = true;
                        break;
                    }

                    int numberOsRequirement = 0;
                    int numberOsPc = 0;
                    Int32.TryParse(Os, out numberOsRequirement);
                    Int32.TryParse(Regex.Replace(systemConfiguration.Os, "[^.0-9]", "").Trim(), out numberOsPc);
                    if (numberOsRequirement != 0 && numberOsPc != 0 && numberOsPc >= numberOsRequirement)
                    {
                        CheckOs = true;
                        break;
                    }
                }

                bool CheckCpu = false;
                if (requirement.Cpu.Count > 0)
                {
                    foreach (var cpu in requirement.Cpu)
                    {
                        // Intel familly
                        if (cpu.ToLower().IndexOf("intel") > -1)
                        {
                            //logger.Debug($"cpu intel - {cpu}");

                            // Old processor
                            if (cpu.ToLower().IndexOf("i3") == -1 & cpu.ToLower().IndexOf("i5") == -1 && cpu.ToLower().IndexOf("i7") == -1 && cpu.ToLower().IndexOf("i9") == -1)
                            {
                                CheckCpu = true;
                                break;
                            }
                        }

                        // AMD familly
                        if (cpu.ToLower().IndexOf("amd") > -1)
                        {
                            //logger.Debug($"cpu amd - {cpu}");

                            // Old processor
                            if (cpu.ToLower().IndexOf("ryzen") == -1)
                            {
                                CheckCpu = true;
                                break;
                            }
                        }

                        // Only frequency
                        if ((cpu.ToLower().IndexOf("intel") == -1 || cpu.ToLower().IndexOf("core") == -1) && cpu.ToLower().IndexOf("amd") == -1)
                        {
                            //logger.Debug($"cpu frequency - {cpu}");

                            //Quad-Core CPU 3 GHz (64 Bit)
                            int index = -1;
                            string Clock = cpu.ToLower();
                            //logger.Debug($"Clock - {Clock}");

                            // delete end string
                            index = Clock.IndexOf("ghz");
                            if (index > -1)
                            {
                                Clock = Clock.Substring(0, index).Trim();
                            }
                            //logger.Debug($"Clock - {Clock}");

                            // delete start string
                            string ClockTemp = Clock;
                            for (int i = 0; i < Clock.Length; i++)
                            {
                                if (Clock[i] == ' ')
                                {
                                    ClockTemp = Clock.Substring(i).Trim();
                                }
                            }
                            Clock = ClockTemp;
                            //logger.Debug($"Clock - {Clock}");

                            try
                            {
                                char a = Convert.ToChar(Thread.CurrentThread.CurrentCulture.NumberFormat.NumberDecimalSeparator);
                                Clock = Clock.Replace('.', a).Replace(',', a).Replace("+", "").Trim();
                                if (double.Parse(Clock) * 1000 < (systemConfiguration.CpuMaxClockSpeed * 2))
                                {
                                    CheckCpu = true;
                                    break;
                                }
                            }
                            catch (Exception ex)
                            {
                                Common.LogError(ex, "SystemChecker", $"Error on find clock control - {Clock}");
                            }
                        }

                        // Recent
                        CheckCpu = CheckCpuBetter(cpu, systemConfiguration.Cpu);
                        if (CheckCpu)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    CheckCpu = true;
                }

                bool CheckRam = false;
                //logger.Debug($"CheckRam - {systemConfiguration.Ram} - {requirement.Ram}");
                if (systemConfiguration.Ram >= requirement.Ram)
                {
                    CheckRam = true;
                }

                bool CheckGpu = false;
                if (requirement.Gpu.Count > 0)
                {
                    foreach (var gpu in requirement.Gpu)
                    {
                        Gpu gpuCheck = new Gpu(systemConfiguration, gpu);
                        CheckGpu = gpuCheck.IsBetter();
                        if (CheckGpu)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    CheckGpu = true;
                }

                bool CheckStorage = false;
                foreach (SystemDisk Disk in systemConfiguration.Disks)
                {
                    //logger.Debug($"CheckStorage - {Disk.FreeSpace} - {requirement.Storage}");
                    if (Disk.FreeSpace >= requirement.Storage)
                    {
                        CheckStorage = true;
                        break;
                    }
                }

                bool AllOk = (CheckOs && CheckCpu && CheckRam && CheckGpu && CheckStorage);

                return new CheckSystem
                {
                    CheckOs = CheckOs,
                    CheckCpu = CheckCpu,
                    CheckRam = CheckRam,
                    CheckGpu = CheckGpu,
                    CheckStorage = CheckStorage,
                    AllOk = AllOk
                };
            }

            return new CheckSystem();
        }

        private static bool CheckCpuBetter(string cpuRequirement, string cpuPc)
        {
            bool Result = false;

            cpuRequirement = cpuRequirement.ToLower();
            cpuPc = cpuPc.ToLower();

            int index = 0;

            string CpuRequirementReference = "";
            int CpuRequirementNumber = 0;

            // Intel
            if (cpuRequirement.IndexOf("i3") > -1)
            {
                CpuRequirementReference = cpuRequirement.Substring(cpuRequirement.IndexOf("i3")).Trim();
            }
            if (cpuRequirement.IndexOf("i5") > -1)
            {
                CpuRequirementReference = cpuRequirement.Substring(cpuRequirement.IndexOf("i5")).Trim();
            }
            if (cpuRequirement.IndexOf("i7") > -1)
            {
                CpuRequirementReference = cpuRequirement.Substring(cpuRequirement.IndexOf("i7")).Trim();
            }
            if (cpuRequirement.IndexOf("i9") > -1)
            {
                CpuRequirementReference = cpuRequirement.Substring(cpuRequirement.IndexOf("i9")).Trim();
            }
            index = CpuRequirementReference.IndexOf(" ");
            if (index > -1)
            {
                CpuRequirementReference = CpuRequirementReference.Substring(0, index);
            }
            CpuRequirementReference = CpuRequirementReference.Trim();
            int.TryParse(Regex.Replace(CpuRequirementReference.Replace("i3", "").Replace("i5", "").Replace("i7", "").Replace("i9", ""), "[^.0-9]", "").Trim(), out CpuRequirementNumber);
            CpuRequirementReference = CpuRequirementReference.Substring(0, 2);

            // AMD



            //logger.Debug($"CpuRequirementReference - {CpuRequirementReference}");
            //logger.Debug($"CpuRequirementNumber - {CpuRequirementNumber}");


            string CpuPcReference = "";
            int CpuPcNumber = 0;

            //Intel(R) Core(TM) i5-4590 CPU @ 3.30GHz
            if (cpuPc.IndexOf("intel") > -1)
            {
                if (cpuPc.IndexOf("i3") > -1)
                {
                    CpuPcReference = cpuPc.Substring(cpuPc.IndexOf("i3"), (cpuPc.Length - cpuPc.IndexOf("i3"))).Trim();
                }
                if (cpuPc.IndexOf("i5") > -1)
                {
                    CpuPcReference = cpuPc.Substring(cpuPc.IndexOf("i5"), (cpuPc.Length - cpuPc.IndexOf("i5"))).Trim();
                }
                if (cpuPc.IndexOf("i7") > -1)
                {
                    CpuPcReference = cpuPc.Substring(cpuPc.IndexOf("i7"), (cpuPc.Length - cpuPc.IndexOf("i7"))).Trim();
                }
                if (cpuPc.IndexOf("i9") > -1)
                {
                    CpuPcReference = cpuPc.Substring(cpuPc.IndexOf("i9"), (cpuPc.Length - cpuPc.IndexOf("i9"))).Trim();
                }
                index = CpuPcReference.IndexOf(" ");
                if (index > -1)
                {
                    CpuPcReference = CpuPcReference.Substring(0, index);
                }
                CpuPcReference = CpuPcReference.Trim();
                int.TryParse(Regex.Replace(CpuPcReference.Replace("i3", "").Replace("i5", "").Replace("i7", "").Replace("i9", ""), "[^.0-9]", "").Trim(), out CpuPcNumber);
                CpuPcReference = CpuPcReference.Substring(0, 2);

                if (int.Parse(CpuPcReference.Replace("i","")) == int.Parse(CpuRequirementReference.Replace("i", "")))
                {
                    if (CpuPcNumber >= CpuRequirementNumber)
                    {
                        Result = true;
                    }
                }

                if (int.Parse(CpuPcReference.Replace("i", "")) > int.Parse(CpuRequirementReference.Replace("i", "")))
                {
                    Result = true;
                }

                if (CpuPcReference == "i3" && CpuRequirementReference == "i5")
                {
                    if (CpuPcNumber >= (CpuRequirementNumber * 2))
                    {
                        Result = true;
                    }
                }
                if (CpuPcReference == "i3" && CpuRequirementReference == "i7")
                {
                    if (CpuPcNumber >= (CpuRequirementNumber * 3))
                    {
                        Result = true;
                    }
                }
                if (CpuPcReference == "i5" && CpuRequirementReference == "i7")
                {
                    if (CpuPcNumber >= (CpuRequirementNumber * 2))
                    {
                        Result = true;
                    }
                }
            }
            // AMD
            else
            {

            }


            //logger.Debug($"CpuPcReference - {CpuPcReference}");
            //logger.Debug($"CpuPcNumber - {CpuPcNumber}");


            return Result;
        }

    }

    public class CheckSystem
    {
        public bool CheckOs { get; set; }
        public bool CheckCpu { get; set; }
        public bool CheckRam { get; set; }
        public bool CheckGpu { get; set; }
        public bool CheckStorage { get; set; }
        public bool? AllOk { get; set; } = null;
    }
}
