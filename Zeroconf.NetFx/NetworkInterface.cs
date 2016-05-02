﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

#if ANDROID
using Android.App;
using Android.Content;
using Android.Net.Wifi;
#endif

namespace Zeroconf
{
    internal class NetworkInterface : INetworkInterface
    {
#if ANDROID
        private readonly WifiManager wifi;

        public NetworkInterface()
        {
            var context = Application.Context.ApplicationContext;
            wifi = (WifiManager)context.GetSystemService(Context.WifiService);
        }
#endif

        public async Task NetworkRequestAsync(byte[] requestBytes,
                                              TimeSpan scanTime,
                                              int retries,
                                              int retryDelayMilliseconds,
                                              Action<string, byte[]> onResponse,
                                              CancellationToken cancellationToken)
        {


            var tasks = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                              .Select(inter =>
                                      NetworkRequestAsync(requestBytes, scanTime, retries, retryDelayMilliseconds, onResponse, inter, cancellationToken))
                              .ToList();

            await Task.WhenAll(tasks)
                      .ConfigureAwait(false);

        }


        async Task NetworkRequestAsync(byte[] requestBytes,
                                              TimeSpan scanTime,
                                              int retries,
                                              int retryDelayMilliseconds,
                                              Action<string, byte[]> onResponse,
                                              System.Net.NetworkInformation.NetworkInterface adapter,
                                              CancellationToken cancellationToken)
        {
            // http://stackoverflow.com/questions/2192548/specifying-what-network-interface-an-udp-multicast-should-go-to-in-net

#if !XAMARIN
            if (!adapter.GetIPProperties().MulticastAddresses.Any())
                return; // most of VPN adapters will be skipped
#endif
            if (!adapter.SupportsMulticast)
                return; // multicast is meaningless for this type of connection

            if (OperationalStatus.Up != adapter.OperationalStatus)
                return; // this adapter is off or not connected

            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                return; // strip out loopback addresses

            var p = adapter.GetIPProperties().GetIPv4Properties();
            if (null == p)
                return; // IPv4 is not configured on this adapter

            var ipv4Address = adapter.GetIPProperties().UnicastAddresses
                                    .FirstOrDefault(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;

            if (ipv4Address == null)
                return; // could not find an IPv4 address for this adapter

            var ifaceIndex = p.Index;

            Debug.WriteLine($"Scanning on iface {adapter.Name}, idx {ifaceIndex}, IP: {ipv4Address}");

            using (var client = new UdpClient())
            {
                for (var i = 0; i < retries; i++)
                {
#if ANDROID
                    var mlock = wifi.CreateMulticastLock("Zeroconf lock");
#endif
                    try
                    {
#if ANDROID
                        mlock.Acquire();
#endif
                        client.Client.SetSocketOption(SocketOptionLevel.IP,
                                                      SocketOptionName.MulticastInterface,
                                                      IPAddress.HostToNetworkOrder(ifaceIndex));



                        client.ExclusiveAddressUse = false;
                        client.Client.SetSocketOption(SocketOptionLevel.Socket,
                                                      SocketOptionName.ReuseAddress,
                                                      true);
                        client.Client.SetSocketOption(SocketOptionLevel.Socket,
                                                      SocketOptionName.ReceiveTimeout,
                                                      scanTime.Milliseconds);
                        client.ExclusiveAddressUse = false;

                        
                        var localEp = new IPEndPoint(IPAddress.Any, 5353);

                        Debug.WriteLine($"Attempting to bind to {localEp} on adapter {adapter.Name}");
                        client.Client.Bind(localEp);
                        Debug.WriteLine($"Bound to {localEp}");

                        var multicastAddress = IPAddress.Parse("224.0.0.251");
                        var multOpt = new MulticastOption(multicastAddress, ifaceIndex);
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multOpt);


                        Debug.WriteLine("Bound to multicast address");

                        // Start a receive loop
                        var shouldCancel = false;
                        var recTask = Task.Run(async
                                               () =>
                                               {
                                                   try
                                                   {
                                                       while (!shouldCancel)
                                                       {
                                                           var res = await client.ReceiveAsync()
                                                                                 .ConfigureAwait(false);
                                                           onResponse(res.RemoteEndPoint.Address.ToString(), res.Buffer);
                                                       }
                                                   }
                                                   catch (ObjectDisposedException)
                                                   {
                                                   }
                                               }, cancellationToken);

                        var broadcastEp = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
                        Debug.WriteLine($"About to send on iface {adapter.Name}");
                        await client.SendAsync(requestBytes, requestBytes.Length, broadcastEp)
                                    .ConfigureAwait(false);
                        Debug.WriteLine($"Sent mDNS query on iface {adapter.Name}");


                        // wait for responses
                        await Task.Delay(scanTime, cancellationToken)
                                  .ConfigureAwait(false);
                        shouldCancel = true;
#if CORECLR
                        client.Dispose();
#else
                        client.Close();
#endif

                        Debug.WriteLine("Done Scanning");


                        await recTask.ConfigureAwait(false);

                        return;
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"Execption with network request, IP {ipv4Address}\n: {e}");
                        if (i + 1 >= retries) // last one, pass underlying out
                            throw;
                    }
                    finally
                    {
#if ANDROID
                        mlock.Release();
#endif
                    }

                    await Task.Delay(retryDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public Task ListenForAnnouncementsAsync(Action<AdapterInformation, string, byte[]> callback, CancellationToken cancellationToken)
        {
            return Task.WhenAll(System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
#if !XAMARIN
                                      .Where(a => a.GetIPProperties().MulticastAddresses.Any())
#endif
                                      .Where(a => a.SupportsMulticast)
                                      .Where(a => a.OperationalStatus == OperationalStatus.Up)
                                      .Where(a => a.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                                      .Where(a => a.GetIPProperties().GetIPv4Properties() != null)
                                      .Where(a => a.GetIPProperties().UnicastAddresses.Any(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork))
                                      .Select(inter => ListenForAnnouncementsAsync(inter, callback, cancellationToken)));
        }

        Task ListenForAnnouncementsAsync(System.Net.NetworkInformation.NetworkInterface adapter, Action<AdapterInformation, string, byte[]> callback, CancellationToken cancellationToken)
        {
            return Task.Factory.StartNew(async () =>
            {
                var ipv4Address = adapter.GetIPProperties().UnicastAddresses
                                         .First(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;

                if (ipv4Address == null)
                    return;

                var ifaceIndex = adapter.GetIPProperties().GetIPv4Properties()?.Index;
                if (ifaceIndex == null)
                    return;

                Debug.WriteLine($"Scanning on iface {adapter.Name}, idx {ifaceIndex}, IP: {ipv4Address}");

                using (var client = new UdpClient())
                {
#if ANDROID
                    var mlock = wifi.CreateMulticastLock("Zeroconf listen lock");
#endif
                    try
                    {
#if ANDROID
                        mlock.Acquire();
#endif
                        client.Client.SetSocketOption(SocketOptionLevel.IP,
                                                      SocketOptionName.MulticastInterface,
                                                      IPAddress.HostToNetworkOrder(ifaceIndex.Value));

                        client.Client.SetSocketOption(SocketOptionLevel.Socket,
                                                      SocketOptionName.ReuseAddress,
                                                      true);
                        client.ExclusiveAddressUse = false;


                        var localEp = new IPEndPoint(IPAddress.Any, 5353);
                        client.Client.Bind(localEp);

                        var multicastAddress = IPAddress.Parse("224.0.0.251");
                        var multOpt = new MulticastOption(multicastAddress, ifaceIndex.Value);
                        client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, multOpt);

                        while (!cancellationToken.IsCancellationRequested)
                        {
                            var packet = await client.ReceiveAsync().ConfigureAwait(false);
                            try
                            {
                                callback(new AdapterInformation(ipv4Address.ToString(), adapter.Name), packet.RemoteEndPoint.Address.ToString(), packet.Buffer);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Callback threw an exception: {ex}");
                            }
                        }

#if CORECLR
                        client.Dispose();
#else
                        client.Close();
#endif

                        Debug.WriteLine("Done listening for mDNS packets.");

                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    finally
                    {
#if ANDROID
                    mlock.Release();
#endif
                    }
                }
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }
}
