﻿using System;
using System.IO;
using System.IO.Ports;
using System.IO.Pipes;
using System.Collections;
using System.Collections.Generic;
using CommandLine;
using System.Threading;
using System.Net;

class Program
{
    private const long k_ticks_per_us = 10;     // Tick is 100ns
    private const int k_header_size = 8;
    private static bool bPipeOpend = false;

    private static NamedPipeServerStream _pipe;
    private static BinaryWriter _writer;
    private static Queue<Buffer> _fifo = new Queue<Buffer>();
    private static List<SerialParser> _ser_par = new List<SerialParser>();

    static void Main(string[] args)
    {
        // Parse command line arguments
        Arguments ar = new Arguments(args);
        if (ar["p1"] == null && ar["p2"] == null)
        {
            Console.WriteLine("Application monitors up to 2 serial ports and sends received data");
            Console.WriteLine("To the Wireshark via named pipe");
            Console.WriteLine();
            Console.WriteLine("Parameters: ");
            Console.WriteLine("   -p1 Serial port #1");
            Console.WriteLine("   -p1b Serial port #1 baudrate (default 19200)");
            Console.WriteLine("   -p1m Serial port #1 match sequence (hex, optional)");
            Console.WriteLine("   -p1t Serial port #1 timeout in us (default 1ms)");
            Console.WriteLine("   -p2 Serial port #2");
            Console.WriteLine("   -p2b Serial port #2 baudrate (default 19200)");
            Console.WriteLine("   -p2m Serial port #2 match sequence (hex, optional)");
            Console.WriteLine("   -p2t Serial port #2 timeout in us (default 1ms)");
            Console.WriteLine("Note: at least one serial port is required, other parameters are optional");

            return;
        }

        // Create serial parsers
        if (ar["p1"] != null)
        {
            int baudrate;
            if (ar["p1b"] == null || !int.TryParse(ar["p1b"], out baudrate))
            {
                baudrate = 19200;
            }

            int timeout_us;
            if (ar["p1t"] == null || !int.TryParse(ar["p1t"], out timeout_us))
            {
                // 1ms default timeout
                timeout_us = 1000;
            }

            List<byte> match = null;
            if (ar["p1m"] != null)
            {
                match = HexStringToList(ar["p1m"]);
            }

            SerialParser sp = new SerialParser(match, ar["p1"], baudrate, timeout_us, _fifo);            
            _ser_par.Add(sp);
        }

        if (ar["p2"] != null)
        {
            int baudrate;
            if (ar["p2b"] == null || !int.TryParse(ar["p2b"], out baudrate))
            {
                baudrate = 19200;
            }

            int timeout_us; 
            if (ar["p2t"] == null || !int.TryParse(ar["p2t"], out timeout_us))
            {
                // 1ms default timeout
                timeout_us = 1000;
            }

            List<byte> match = null;
            if (ar["p2m"] != null)
            {
                match = HexStringToList(ar["p2m"]);
            }

            SerialParser sp = new SerialParser(match, ar["p2"], baudrate, timeout_us, _fifo);
            _ser_par.Add(sp);
        }

        // Run till ESC
        Console.WriteLine("Press ESC to exit");
        do
        {
            while (!Console.KeyAvailable)
            {
                if (bPipeOpend == false)
                {
                    // create pipe
                    try
                    {
                        _pipe = new NamedPipeServerStream("wireshark", PipeDirection.Out);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine();
                        Console.WriteLine("Error creating pipe : " + ex.Message);
                        return;
                    }

                    // wait for wireshark to connect to pipe
                    Console.WriteLine("Waiting for connection to wireshark");
                    Console.Write(@"Open wireshark and connect to interface: \\.\pipe\wireshark");
                    _pipe.WaitForConnection();
                    bPipeOpend = true;
                    Console.WriteLine("Wireshark is connected");

                    // connect binary writer to pipe to write binary data into it
                    _writer = new BinaryWriter(_pipe);

                    // Write global header
                    WriteToPipe(BitConverter.GetBytes((UInt32)0xa1b2c3d4)); // Magic number
                    WriteToPipe(BitConverter.GetBytes((UInt16)2));  // Major version
                    WriteToPipe(BitConverter.GetBytes((UInt16)4));  // Minor version
                    WriteToPipe(BitConverter.GetBytes((Int32)0));   // Timezone 0 - UTC
                    WriteToPipe(BitConverter.GetBytes((UInt32)0));  // Timestamp accuracy - unused
                    WriteToPipe(BitConverter.GetBytes((UInt32)65535));  // Maximum lenght of captured packets 
                    WriteToPipe(BitConverter.GetBytes((UInt32)147));    // Data Link Type (DLT) - reserved
                    foreach (SerialParser sp in _ser_par)
                    {
                        sp.Open();
                    }
                }
                if (_fifo.Count > 0)
                {
                    Buffer bf = _fifo.Dequeue();
                    WritePacket(bf);
                }
                else
                {
                    Thread.Sleep(1);
                }

                foreach (SerialParser sp in _ser_par)
                {
                    sp.Run();
                }
            }
        } while (Console.ReadKey(true).Key != ConsoleKey.Escape);

        // Closing pipe
        Console.WriteLine();
        Console.Write("Exiting application ..");
        foreach (SerialParser sp in _ser_par)
        {
            sp.Close();
        }
        _writer.Close();
        _pipe.Close();
        Console.WriteLine(".Ok");
    }


    // ============================================================
    // Private functions
    // ============================================================

    /// <summary>
    /// Writing packet to the pipe
    /// </summary>
    /// <param name="bf"></param>
    static private void WritePacket(Buffer bf)
    {
        // Write Packet header
        TimeSpan epoch_ts = bf.timestamp_dt - new DateTime(1970, 1, 1); // Epoch time as timespan
        UInt32 epoch_s = (UInt32)epoch_ts.TotalSeconds;  // Epoch time in s        

        // Epoch time 
        WriteToPipe(BitConverter.GetBytes(epoch_s));

        // Offset in us
        long epoch_ticks = epoch_ts.Ticks;  // Epoch time in ticks
        long epoch_us = epoch_ticks / k_ticks_per_us;
        UInt32 us = (UInt32)(epoch_us - (epoch_s * 1000 * 1000));
        WriteToPipe(BitConverter.GetBytes(us));

        // Number of bytes saved to file
        UInt32 incl_len = (UInt32)bf.byte_buff.Count + k_header_size;
        WriteToPipe(BitConverter.GetBytes(incl_len));

        // Number of bytes captured
        UInt32 orig_len = (UInt32)bf.byte_buff.Count + k_header_size;
        WriteToPipe(BitConverter.GetBytes(orig_len));

        // Add UDP header
        // Source port
        Int16 src_port = IPAddress.HostToNetworkOrder((Int16)bf.PortNumber);
        WriteToPipe(BitConverter.GetBytes(src_port));

        // Destination port
        Int16 dst_port = IPAddress.HostToNetworkOrder((Int16)bf.PortNumber);
        WriteToPipe(BitConverter.GetBytes(dst_port));

        // Lenght 
        Int16 len = IPAddress.HostToNetworkOrder((Int16)incl_len);
        WriteToPipe(BitConverter.GetBytes(len));

        // Checksum (should be optional)
        Int16 crc = IPAddress.HostToNetworkOrder((Int16)0);
        WriteToPipe(BitConverter.GetBytes(crc));

        // Send actual packet to pipe
        WriteToPipe(bf.byte_buff.ToArray());
    }

    private static void WriteToPipe(byte[] b)
    {
        // Send to pipe
        if (bPipeOpend == false) return;
        try
        {
            for (int i = 0; i < b.Length; i++)
            {
                _writer.Write(b[i]);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("Writing to pipe exception: " + ex.Message);
            foreach (SerialParser sp in _ser_par)
            {
                sp.Close();
            }
            _writer.Close();
            _pipe.Close();     
            bPipeOpend = false;
        }
    }


    /// <summary>
    /// Convert string (hex values) to array of bytes
    /// </summary>
    /// <param name="hex"></param>
    /// <returns></returns>
    private static List<byte> HexStringToList(String hex)
    {
        List<byte> lst = new List<byte>();
        if (hex.Length % 2 == 1)
        {
            // Should be even amount of nibbles
            return lst;
        }            

        // Parse the string
        for (int i = 0; i < hex.Length >> 1; ++i)
        {
            lst.Add((byte)((GetHexVal(hex[i << 1]) << 4) + (GetHexVal(hex[(i << 1) + 1]))));
        }

        return lst;
    }


    /// <summary>
    /// Get 
    /// </summary>
    /// <param name="hex"></param>
    /// <returns></returns>
    private static int GetHexVal(char hex)
    {
        // Parse both upper case and lower case
        int ret = (int)hex;        
        if (ret < 58)
        {
            ret -= 48;
            return ret;
        }
        else
        {
            ret -= 55;
            return ret;
        }
    }
}