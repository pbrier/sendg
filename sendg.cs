/*
 * sendg.cs
 * Send gcode to Ultimaker via serial port.
 * (c) 2011 Peter Brier. pbrier.nl - gmail.com
 *
 * Requires Mono or MS C# to compile (mono-gmcs is only required to compile). For example:
 * # sudo apt-get install mono-gmcs mono-gac mono-utils
 * # gmcs sendg.cs
 * # mono sendg.exe
 *
 * USE: sendg -d -l -c[buffercount] -p[portname] -b[baudrate] [filename]
 * -d             Enable debugging
 * -l             Enable logging
 * -e             Enable build time estimation
 * -c[n]          Set delayed ack count (1 is no delayed ack)
 * -p[name]       Specify portname (COMx on windows, /dev/ttyAMx on linux)
 * -b[baudrate]   Set baudrate (default is 115200)
 * [filename]     The GCODE file to send
 * 
 * Best results are obtained when c>3, however, your firmware serial buffer should be large 
 * enough to hold all these lines
 *
 * Two threads are used (a reader and main thread).
 * Note: we do *NOT* use the async events of the SerialPort object.
 * these have 'issues' on mono (one of the 'issues' is that they don't work).
 *
 * Upto "bufcount" lines are sent, before they are ACK-ed
 * A semaphore is used to sync the threads.
 * We assume all these lines fit in the 128 byte serial buffer of
 * the arduino. An optimized version could cound the actual nr of
 * bytes sent and ACKed to sync the reader/writer.
 * No CRC, line-numbers or resents are implemented.
 * Tested with standard ultimaker and sprinter firmware, 
 * using bufcount=2
 *
 * sendg is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 * sendg is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with sendg.  If not, see <http://www.gnu.org/licenses/>.
 */
#define WINDOWS
 
using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using System.IO;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace sendg
{
  class Program
  {
    static Thread r; // reader thread
    static Semaphore sem;
    static SerialPort port;
    static bool debug = false, log = false, progress=false, quit=false;
    static long t0 = 0;

    private static Stopwatch sw = Stopwatch.StartNew();
    private static long msec() { return sw.ElapsedMilliseconds; }

    /*
     * Reader thread
     * Read data and find EOL chars
     * When one is found: the line is displayed
     */
    private static void Reader ()
    {
      string s = "", n = "";
      long recnr = 0,t1=0;
      try {
      while (true) {
        n += ((char)port.ReadChar ()).ToString ();
        while (port.BytesToRead > 0)
        {
          n += port.ReadExisting ();
        }
        while (n.Contains ("\n")) {
          t1 = msec();
          int p = n.IndexOf ("\n");
          s = n.Substring (0, p);
          n = n.Substring (p + 1, n.Length - p - 1);
          if (s.Length > 0)
          {
            try {
              sem.Release (1);
              recnr++;
              if ( debug ) Console.WriteLine ((t1-t0).ToString()  + " " + recnr.ToString() + " < " + s);
            } catch { // unexpected data?
              if( debug ) Console.WriteLine ((t1-t0).ToString()  + " " + recnr.ToString()  + " << " + s);
            }
          }
        }
      } 
      } catch { // catch all, used for thread abort exception
        if ( !quit ) 
          Console.WriteLine("Some error during read");
      }
    }

		static void Help()
		{
		  Console.WriteLine(
			"\nsendg: Send GCODE file to your 3D printer via serial port. \n" +
			"(c) 2011 Peter Brier.\n" + 
			"sendg is free software: you can redistribute it and/or modify it\n" +
			"under the terms of the GNU General Public License as published\n" + 
			"by the Free Software Foundation\n" +
			"\nUSE: sendg -d -l -c[buffercount] -p[portname] -b[baudrate] [filename]\n\n" +
		  " -r             Enable realtime process priority\n" +
		  " -d             Enable debugging (show lots of debug messages)\n" +
		  " -l             Enable logging (show time in msec, linenr and data)\n" +
      " -e             Show built time estimation (in minutes)\n" +
			" -c[n]          Set delayed ack count (1 is no delayed ack, default is 4)\n" +
      " -p[name]       Specify portname (COMx on windows, /dev/ttyAMx on linux)\n" +
      " -b[baudrate]   Set baudrate (default is 115200)\n" +
      " [filename]     The GCODE file to send\n"
			);
		}
		
    /*
     * Main function
     * Open port, read file and send to SerialPort
     */
    static void Main (string[] args)
    {
      string portname = "COM15";    
      string filename = null;
      int baudrate = 115200;
      int bufcount = 4; // nr of lines to buffer
      long t1=0;
			bool realtime = false;
			
    // Parse cmd line
		  if ( args.Length < 1 ) 
		  {
		    Help();
			  return;
		  }
      foreach(string arg in args)
      {
        if ( arg[0] != '-' )
        {
          filename = arg;
          continue;
        }
        string val = arg.Substring(2);
        string key = arg.Substring(1,1);

        switch (key)
        {
          case "c": Int32.TryParse (val, out bufcount); break;
          case "b": Int32.TryParse (val, out baudrate); break;
          case "p": portname = val; break;
          case "d": debug = true; break;
          case "e": progress = true; break;
          case "l": log = true; break;
					case "r": realtime = true; break;
          default:
            Console.WriteLine("SENDG: Unknown option: " + key );
						Help();
            return;
        }
      }
      if ( debug )
      {
        Console.WriteLine("Port: " + portname + " " + baudrate.ToString() + "bps");
        Console.WriteLine("File: " + filename + " buffer:" + bufcount.ToString ());
				Console.WriteLine("Realtime priority: " + (realtime ? "ENABLED" : "DISABLED") );
      }
			
			if ( realtime ) 
			  using (Process p = Process.GetCurrentProcess())
			    p.PriorityClass = ProcessPriorityClass.RealTime;
			
      try
      {
        // open port and wait for Arduino to boot
        port = new SerialPort(portname, baudrate);
        port.Open();
        port.NewLine = "\n";
        port.DtrEnable = true;
        port.RtsEnable = true;
      }
      catch( Exception ex )
      {
        Console.WriteLine("SENDG: Cannot open serial port (Portname=" + portname + ")");
        if (debug )
          Console.WriteLine( ex.ToString() );
        return;
      }
      Thread.Sleep (2000);      
      
    // Init semaphore and Start 2nd thread
      sem = new Semaphore (0, bufcount);
      sem.Release (bufcount);
      r = new Thread (Reader);
      t0 = msec();
      r.Start (); 
      
      
    // Send all lines in the file
      string line;
      int linenr = 1;
      t0 = msec();
      StreamReader reader;
      try
      {
        reader = new StreamReader (filename);
      }
      catch ( Exception ex )
      {
        Console.WriteLine("SENDG: Cannot open file! (filename=" + filename + ")");
        if (debug) Console.WriteLine(ex.ToString());
        r.Abort();
        return;
      }
        while ((line = reader.ReadLine ()) != null)
        {
          string l = Regex.Replace(line, @"[;(]+.*[\n)]*", ""); // remove comment
          l = l.Trim();
          if ( l.Length > 0 )
          {
            linenr++;
            line = l + "\n";
            sem.WaitOne ();
            port.Write (line);
            // 20 min, 10%, total = 2min/%, total = 200 min
            t1 = msec();
            double time = (t1-t0) / 60000.0; // elapsed time in minutes
            double cur = (100.0 * reader.BaseStream.Position / (double)reader.BaseStream.Length); // current percentage
            double total = 100.0 * (time / (double)cur); // remaining time in min
            time = Math.Round(time);
            total = Math.Round(total);
            double remaining = total - time;
            cur = Math.Floor(cur);
            if (progress)
            {
              Console.WriteLine(time.ToString() + "min: Line " + linenr.ToString() +  " (" + cur.ToString() + "%) Remaining=" + remaining.ToString() + "min, Total=" + (total).ToString() + "min");
            }
            if( log ) 
            {
              Console.WriteLine ((t1-t0).ToString()  + " " + linenr.ToString() + " > " + line);
            }
          }
        }
      
    // Wait for the last line to complete (1sec fixed time) and abort thread
      long e = msec() - t0;
      quit = true;
      Thread.Sleep (1000);
      port.Close ();
      r.Abort ();
      Console.WriteLine ("Toal time: " + e.ToString () + " msec");
    }
  }
}


