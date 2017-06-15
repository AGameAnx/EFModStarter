﻿using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;
using System.Management;
using System.IO;
using Steamworks;
using Newtonsoft.Json;
using System.Collections.Generic;

/*-----------------------------------------------------------------------
-- Company of Heroes: Eastern Front Steam Starter
--
-- (c) ARCH Ltd. - Archaic Entertainment Ltd. 2016
--
-- Programmers: Walentin 'Walki' L.
-----------------------------------------------------------------------
-- To-Do: Update images
-----------------------------------------------------------------------
INFO: 
-- Remember that this executable is located in the RelicCoH folder
-- This program will register the player in our database
-- Then it will call Steam to start RelicCoH with the Eastern Front mod
-- 
-----------------------------------------------------------------------
*/

namespace CoHEF
{
    class CoHEF
    {
        // Variables
        static int timeouttime = 10;
        static int timeouttime_end = 8;
        static string steamfilename = "Steam";
        static string cohfilename = "RelicCoH";
        static string cohfilename2 = "RelicCoH.exe";
        static string bugsplat_report = "BsSndRpt";
        static string ef_version_locale_key = "18030216";

        static string auth_key = ""; // Removed for GitHub release

        // NOTE: -unqiuetoken is only a "marker" to find the right process in case of more than one RelicCoH Process
        static string steamarguments = "-applaunch 228200 -dev -mod EF_beta -uniquetoken";

        static void Main(string[] args)
        {
            // Make sure we can only have one running at a time
            if (Process.GetProcessesByName(Path.GetFileNameWithoutExtension(System.Reflection.Assembly.GetEntryAssembly().Location)).Length > 1) Environment.Exit(0);

            checklocation(); // Make sure EF is installed in the right place
            string version_string = getversion(); // Get EF version
            string file = getpipelinedir(); // Get the achievement pipeline.dat
            File.WriteAllText(file, string.Empty); // Empty the pipeline.dat; or create it
            
            foreach (string arg in args)
            {
                if (arg == "-cheat")
                {
                    bool steamapi_state = SteamAPI.Init();
                    bool achievements_state = SteamUserStats.RequestCurrentStats();
                    if (steamapi_state && achievements_state)
                    {
                        SteamUserStats.SetAchievement("cheatmod");
                        SteamUserStats.StoreStats();
                    }
                    SteamAPI.Shutdown();
                }
                else if (arg == "-daemonmode")
                {
                    var webClient = new System.Net.WebClient();
                    string url = "http://me2stats.eu:5020/join?version=" + version_string + "&key=" + auth_key;
                    // Tell server that we started the game
                    try
                    {
                        webClient.DownloadStringAsync(new Uri(url));
                    }
                    catch (Exception)
                    {
                        // Suppress weberrors, could be no internet etc.
                    }

                    bool steamapi_state = SteamAPI.Init();
                    bool achievements_state = SteamUserStats.RequestCurrentStats();
                    // Use this to reset all achievements
                    // SteamUserStats.ResetAllStats(true); 
                    // SteamUserStats.StoreStats();
                    // Stuff we need for the loop
                    int timer = 0;
                    Process[] coh_processes;
                    Process[] bugsplat_processes;
                    Process Unique_Process = null;

                    // Prepare in case we have more than two processes
                    string wmiQuery = string.Format("select ProcessId, Name, CommandLine from Win32_Process where Name='{0}'", cohfilename);
                    // Start scanning for RelicCOH.exe
                    while (true)
                    {
                        // Can only be one really, because Company of Heroes will only allow one
                        coh_processes = Process.GetProcessesByName(cohfilename);

                        if (coh_processes.Length > 0)
                        {
                            ManagementClass mgmtClass = new ManagementClass("Win32_Process");

                            foreach (ManagementObject process in mgmtClass.GetInstances())
                            {
                                // Basics - process name & pid
                                string processName = process["Name"].ToString().ToLower();
                                if ((cohfilename2.ToLower()) == processName)
                                {
                                    uint pid = (uint)process["ProcessId"];
                                    Unique_Process = Process.GetProcessById((int)pid);
                                    // Get the command line - can be null if we don't have permissions
                                    string cmdLine = null;
                                    if (process["CommandLine"] != null)
                                    {
                                        cmdLine = process["CommandLine"].ToString();

                                        if (cmdLine.Contains("-uniquetoken"))
                                            goto endOfLoop; // Yes... fucking goto
                                    }
                                }
                            }
                        }
                        // Wait 2000 ticks
                        Thread.Sleep(2000);
                        timer++;

                        // We failed to find the process... Exit
                        if (timer >= timeouttime)
                        {
                            SteamAPI.Shutdown();
                            Environment.Exit(1);
                        }
                    }

                    endOfLoop:

                    if (steamapi_state)
                    {
                        // Update steamid (for custom scar function)
                        CSteamID steamid = SteamUser.GetSteamID();
                        string scarfile = getscardir();

                        string[] lines = File.ReadAllLines(scarfile);
                        for (int i = 0; i != lines.Length; i++)
                        {
                            if (lines[i].Contains("gSteamID"))
                                lines[i] = "gSteamID=" + steamid;
                        }
                        File.WriteAllLines(scarfile, lines);
                    }
                    
                    if (steamapi_state && achievements_state)
                    {
                        // Both api and achievements are active 
                        int checkrate = 4000;
                        DateTime dt_before = File.GetLastWriteTime(file);
                        DateTime dt_after = File.GetLastWriteTime(file);
                        while (!Unique_Process.HasExited)
                        {
                            // Check if the pipeline.dat has been edited
                            dt_before = File.GetLastWriteTime(file);
                            if (dt_before != dt_after)
                            {
                                dt_after = File.GetLastWriteTime(file);
                                try
                                {
                                    // There's only one line so no need for foreach
                                    string[] lines = File.ReadAllLines(file);

                                    if (lines.Length > 0)
                                    {
                                        // Decode the line
                                        RootObject converted = JsonConvert.DeserializeObject<RootObject>(lines[0]);
                                        List<List<string>> achievements_bool = add_properties(converted);
                                        bool changes = false;
                                        // Iterate the boolean achievements
                                        foreach (List<string> list in achievements_bool)
                                        {
                                            if (list[1] == "1") // Did he/she get the achievement? index 1 is always the achievement status
                                            {
                                                string api_name = list[0]; // index 0 is always the achievement api_name
                                                if (SteamUserStats.SetAchievement(api_name))
                                                    changes = true; // Set true, so we will commit after we iterated all achievements
                                            }
                                        }

                                        if (changes)
                                        {
                                            SteamUserStats.StoreStats();
                                            changes = false;
                                        }
                                    }
                                }
                                catch (IOException)
                                {
                                    // the file is unavailable because it is: still being written to or being processed by another thread
                                    // this collision might happen again so change the duration at which we check
                                    checkrate++;
                                    if (checkrate > 5000)
                                        checkrate = 4000;
                                }
                            }
                            Thread.Sleep(checkrate);
                        }
                        File.WriteAllText(file, string.Empty); // Empty the pipeline.dat
                    }
                    else
                    {
                        // One of the APIs didn't load so just wait for exit
                        Unique_Process.WaitForExit();
                    }

                    // Tell the server we shutdown the game
                    url = "http://me2stats.eu:5020/leave?key=" + auth_key;

                    try
                    {
                        webClient.DownloadStringAsync(new Uri(url));
                    }
                    catch (Exception)
                    {
                        // Suppress weberrors, could be no internet etc.
                    }

                    // Reset timer
                    timer = 0;

                    // CoH has closed... Did it crash?
                    // If yes we have to keep running until the BugReport is sent
                    // Start scanning for BsSndRpt.exe
                    while (true)
                    {
                        bugsplat_processes = Process.GetProcessesByName(bugsplat_report);
                        // There can only be one
                        if (bugsplat_processes.Length > 0)
                        {
                            // Tell the server that the game crashed
                            url = "http://me2stats.eu:5020/reportcrash?version=" + version_string + "&key=" + auth_key;
                            try
                            {
                                webClient.DownloadStringAsync(new Uri(url));
                            }
                            catch (Exception)
                            {
                                // Suppress weberrors, could be no internet etc.
                            }
                            bugsplat_processes[0].WaitForExit();
                            break;
                        }

                        // Wait 1000 Ticks
                        Thread.Sleep(1000);
                        timer++;

                        // We failed to find the process... So no crash
                        if (timer >= timeouttime_end)
                        {
                            SteamAPI.Shutdown();
                            Environment.Exit(0);
                        }
                    }
                    SteamAPI.Shutdown();
                    Environment.Exit(0);
                }
            }

            Process[] steam_processes;

            // Try to find our Steam process
            steam_processes = Process.GetProcessesByName(steamfilename);

            if (steam_processes.Length == 0)
            {
                // Message box "Steam not running!"
                MessageBox.Show("Steam is not running!", "Company of Heroes: Eastern Front", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(0);
            }
            else if (steam_processes.Length > 1)
            {
                // Message box "Multiple Steam processes found!"
                if (MessageBox.Show("Multiple Steam processes found! This may lead to unexpected behaviour! Continue?", "Company of Heroes: Eastern Front", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                    Environment.Exit(0); // User chose to abort
            }

            string steam_fullPath = steam_processes[0].MainModule.FileName;

            // Setup our Steam process 
            Process steam_process = new Process();

            // Set our Steam exe path
            steam_process.StartInfo.FileName = steam_fullPath;
 
            // Did the starter get arguments supplied?
            if (args.Length > 0)
            {
                // Yes, append them to our other options
                foreach (string s in args)
                {
                    // Add whitespace
                    steamarguments += " ";
                    // Append argument
                    steamarguments = steamarguments + s;
                }
            }

            // Set starting arguments
            steam_process.StartInfo.Arguments = steamarguments;

            // Start CoH:EF over Steam
            steam_process.Start();

            // Start EFDaemon
            Process EFDaemon = new Process();
            EFDaemon.StartInfo.FileName = "EFDaemon.exe";
            EFDaemon.StartInfo.Arguments = "-daemonmode";
            try
            {
                EFDaemon.Start();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                EFDaemon.StartInfo.FileName = "EF_Bin\\EFDaemon.exe";
                EFDaemon.Start();
            }
        }

        static void checklocation()
        {
            // Check if coh is installed here
            string path_to_dir = Directory.GetCurrentDirectory();
            string path_to_module = path_to_dir + "\\RelicCoH.module";

            if (!File.Exists(path_to_module))
            {
                path_to_dir = Path.GetFullPath(Path.Combine(path_to_dir, @"..\"));
                path_to_module = path_to_dir + "\\RelicCoH.module";
                if (!File.Exists(path_to_module))
                {
                    if (MessageBox.Show("Unable to find RelicCoH! \nMake sure Company of Heroes: Eastern Front is installed in the same directory as Company of Heroes (New Steam Version)!", "Company of Heroes: Eastern Front", MessageBoxButtons.OK, MessageBoxIcon.Error) == DialogResult.OK)
                        Environment.Exit(0); // Exit
                }
            } 
        }

        static string getscardir()
        {
            string path_to_dir = Directory.GetCurrentDirectory();
            string path_to_scar = path_to_dir + "\\EF_beta\\Data\\scar\\steam.scar";

            if (!File.Exists(path_to_scar))
            {
                path_to_dir = Path.GetFullPath(Path.Combine(path_to_dir, @"..\"));
                path_to_scar = path_to_dir + "\\EF_beta\\Data\\scar\\steam.scar";
            }
            return path_to_scar;
        }

        static string getpipelinedir()
        {
            string path_to_dir = Directory.GetCurrentDirectory();
            string path_to_scar = path_to_dir + "\\pipeline.dat";

            if (!File.Exists(path_to_scar))
            {
                path_to_dir = Path.GetFullPath(Path.Combine(path_to_dir, @"..\"));
                path_to_scar = path_to_dir + "\\pipeline.dat";
            }
            return path_to_scar;
        }

        static string getversion()
        {
            // Get Eastern Front version
            string path_to_dir;
            path_to_dir = Directory.GetCurrentDirectory();
            // Append the path to the locale
            string path_to_locale = path_to_dir + "\\EF_beta\\Locale\\English\\Eastern_Front.English.ucs";

            string version_string = "";
            string line;
            StreamReader file;
            // Open file and read lines
            try
            {
                file = new StreamReader(path_to_locale);
            }
            catch (DirectoryNotFoundException)
            {
                path_to_dir = Path.GetFullPath(Path.Combine(path_to_dir, @"..\"));
                path_to_locale = path_to_dir + "\\EF_beta\\Locale\\English\\Eastern_Front.English.ucs";
                file = new StreamReader(path_to_locale);
            }

            using (file)
            {
                while ((line = file.ReadLine()) != null)
                {
                    if (line.Contains(ef_version_locale_key))
                    {
                        // We found our version line
                        version_string = line;

                        // Cut the version
                        version_string = version_string.Substring(version_string.Length - 7);
                        break;
                    }
                    else
                    {
                        // If we failed to find then it's 0.0.0.0 = undefined
                        version_string = "0.0.0.0";
                    }
                }
            }
            // Remove dots
            version_string = version_string.Replace(".", "");
            return version_string;
        }

        static List<List<string>> add_properties(RootObject p_obj)
        {
            List<List<string>> achievements_bool = new List<List<string>>();
            achievements_bool.Add(p_obj.feld_steiner);
            achievements_bool.Add(p_obj.churchill);
            achievements_bool.Add(p_obj.woroshilov);
            achievements_bool.Add(p_obj.hitler_cats);
            achievements_bool.Add(p_obj.downfall);
            achievements_bool.Add(p_obj.red_army);
            achievements_bool.Add(p_obj.compstomp);
            achievements_bool.Add(p_obj.mohairborne);
            achievements_bool.Add(p_obj.jungleking);
            achievements_bool.Add(p_obj.volkssturm);
            achievements_bool.Add(p_obj.danko);
            achievements_bool.Add(p_obj.pershing2);
            achievements_bool.Add(p_obj.collateral);
            achievements_bool.Add(p_obj.vet4);
            achievements_bool.Add(p_obj.kettenkrad);
            achievements_bool.Add(p_obj.isu);
            achievements_bool.Add(p_obj.pershing2);
            achievements_bool.Add(p_obj.r_btn);
            achievements_bool.Add(p_obj.heroes);
            achievements_bool.Add(p_obj.stransky);
            achievements_bool.Add(p_obj.lendlease);
            achievements_bool.Add(p_obj.bledforthis);
            achievements_bool.Add(p_obj.crazywilly);
            achievements_bool.Add(p_obj.is2construction);
            achievements_bool.Add(p_obj.jeep);
            achievements_bool.Add(p_obj.wolverine);
            achievements_bool.Add(p_obj.bergetiger);
            achievements_bool.Add(p_obj.beutepanzer);
            achievements_bool.Add(p_obj.rushberlin);
            achievements_bool.Add(p_obj.mg42);
            achievements_bool.Add(p_obj.hummel);
            achievements_bool.Add(p_obj.kv1);
            achievements_bool.Add(p_obj.grup_steiner);
            achievements_bool.Add(p_obj.pershing);
            achievements_bool.Add(p_obj.stormtiger);
            achievements_bool.Add(p_obj.soviet_prod);
            achievements_bool.Add(p_obj.saizew);
            achievements_bool.Add(p_obj.furytiger);
            achievements_bool.Add(p_obj.wittmann);
            achievements_bool.Add(p_obj.bigcats);
            achievements_bool.Add(p_obj.oneman);
            achievements_bool.Add(p_obj.furysherman);
            return achievements_bool;
        }
    }
}
