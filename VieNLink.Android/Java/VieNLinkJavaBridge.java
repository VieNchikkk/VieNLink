package com.vienlink.platform;

import android.net.VpnService;
import android.os.ParcelFileDescriptor;
import android.util.Log;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;

import libbox.CommandServer;
import libbox.InterfaceUpdateListener;
import libbox.Libbox;
import libbox.NetworkInterface;
import libbox.NetworkInterfaceIterator;
import libbox.OverrideOptions;
import libbox.TunOptions;

/**
 * Java-сторона ядра: один CommandServer на процесс, PlatformInterface и TUN.
 *
 * Вызывается из C# только при старте/остановке (через JNI-рефлексию);
 * всё, что ядро дёргает на каждом соединении (protect, DNS, owner), остаётся Java.
 */
public final class VieNLinkJavaBridge {
    private static final String TAG = "VieNLink";

    private static VpnService service;
    private static ParcelFileDescriptor tun;
    private static CommandServer server;
    private static final VieNLinkServerHandler handler = new VieNLinkServerHandler();
    private static final VieNLinkJavaPlatform platform = new VieNLinkJavaPlatform();
    private static InterfaceUpdateListener interfaceListener;

    private VieNLinkJavaBridge() {
    }

    public static void setService(VpnService value) {
        service = value;
        Log.i(TAG, "JavaBridge: VpnService " + (value == null ? "нет" : "готов"));
    }

    public static VpnService getService() {
        return service;
    }

    public static void start(String config) throws Exception {
        if (server == null) {
            server = Libbox.newCommandServer(handler, platform);
            Log.i(TAG, "JavaBridge: CommandServer " + (server == null ? "null" : "создан"));
        }
        if (server == null) {
            throw new IllegalStateException("newCommandServer вернул null");
        }

        server.startOrReloadService(config, new OverrideOptions());
        Log.i(TAG, "JavaBridge: StartOrReloadService выполнен");
    }

    public static void stop() {
        try {
            if (server != null) {
                server.closeService();
                server.close();
            }
        } catch (Throwable t) {
            Log.w(TAG, "JavaBridge: остановка ядра: " + t.getMessage());
        }
        server = null;
        closeTun();
        stopInterfaceMonitor();
        service = null;
        Log.i(TAG, "JavaBridge: остановлен");
    }

    public static void closeTun() {
        try {
            if (tun != null) {
                tun.close();
            }
        } catch (Throwable ignored) {
        }
        tun = null;
    }

    public static boolean hasTun() {
        return tun != null;
    }

    static int openTun(TunOptions options) {
        if (options == null || service == null) {
            return -1;
        }

        try {
            VpnService.Builder builder = service.new Builder()
                    .setSession("VieNLink")
                    .setMtu(options.getMTU() > 0 ? options.getMTU() : 1500);

            // По умолчанию все приложения, кроме себя: split-tunnel настраивается
            // на стороне C# (AppRouteMode), сюда его можно передать позже.
            builder.addDisallowedApplication(service.getPackageName());

            addPrefixes(builder, options.getInet4Address(), false);
            addPrefixes(builder, options.getInet6Address(), false);

            // Маршруты, которыми захватывается трафик приложений. Адреса (172.19.0.1/30)
            // сюда считать нельзя: иначе fallback ниже никогда не сработает и в VPN
            // попадёт только собственный подсет — DNS доходит (на 172.19.0.1), а
            // TCP приложений ходит напрямую мимо TUN.
            int routes = 0;
            routes += addPrefixes(builder, options.getInet4RouteAddress(), true);
            routes += addPrefixes(builder, options.getInet6RouteAddress(), true);
            if (routes == 0) {
                builder.addRoute("0.0.0.0", 0);
            }

            if (!addDns(builder, options.getDNSServerAddress())) {
                builder.addDnsServer("1.1.1.1");
            }

            closeTun();
            tun = builder.establish();
            if (tun == null) {
                Log.w(TAG, "JavaBridge: TUN не создан (нет разрешения VPN?)");
                return -1;
            }

            Log.i(TAG, "JavaBridge: TUN поднят, fd=" + tun.getFd());
            return tun.getFd();
        } catch (Throwable t) {
            Log.e(TAG, "JavaBridge: ошибка TUN", t);
            return -1;
        }
    }

    private static int addPrefixes(VpnService.Builder builder, libbox.RoutePrefixIterator iterator, boolean asRoute) {
        if (iterator == null) {
            return 0;
        }

        int added = 0;
        while (iterator.hasNext()) {
            libbox.RoutePrefix prefix = iterator.next();
            if (prefix == null) {
                continue;
            }

            String address = prefix.address();
            if (address == null || address.isEmpty()) {
                continue;
            }

            if (asRoute) {
                builder.addRoute(address, prefix.prefix());
            } else {
                builder.addAddress(address, prefix.prefix());
            }
            added++;
        }
        return added;
    }

    private static boolean addDns(VpnService.Builder builder, libbox.StringIterator iterator) {
        boolean added = false;
        if (iterator != null) {
            while (iterator.hasNext()) {
                String server = iterator.next();
                if (server != null && !server.isEmpty()) {
                    builder.addDnsServer(server);
                    added = true;
                }
            }
        }
        return added;
    }

    static void protect(int fd) {
        if (service != null) {
            try {
                service.protect(fd);
            } catch (Throwable t) {
                Log.w(TAG, "protect(" + fd + "): " + t.getMessage());
            }
        }
    }

    static boolean protectDatagram(java.net.DatagramSocket socket) {
        return service != null && service.protect(socket);
    }

    static NetworkInterfaceIterator getInterfaces() {
        List<NetworkInterface> list = new ArrayList<>();
        try {
            for (java.net.NetworkInterface source : Collections.list(java.net.NetworkInterface.getNetworkInterfaces())) {
                NetworkInterface item = new NetworkInterface();
                item.setName(source.getName());
                item.setIndex(source.getIndex());
                item.setMTU(1500);
                item.setFlags(source.isUp() ? 1 : 0);
                item.setType(1);
                item.setMetered(false);
                list.add(item);
            }
        } catch (Throwable t) {
            Log.w(TAG, "getInterfaces: " + t.getMessage());
        }
        return new VieNLinkIterators.NetworkInterfaces(list);
    }

    static void startDefaultInterfaceMonitor(InterfaceUpdateListener listener) {
        interfaceListener = listener;
        if (service == null) {
            return;
        }

        android.net.ConnectivityManager cm =
                (android.net.ConnectivityManager) service.getSystemService(android.content.Context.CONNECTIVITY_SERVICE);
        if (cm == null) {
            return;
        }

        refreshDefaultInterface(cm);
        // Динамические изменения подхватываются при следующем открытии/перезапуске:
        // для тестовой сборки достаточно начального интерфейса.
    }

    static void stopInterfaceMonitor() {
        interfaceListener = null;
    }

    private static void refreshDefaultInterface(android.net.ConnectivityManager cm) {
        if (interfaceListener == null) {
            return;
        }

        try {
            for (android.net.Network network : cm.getAllNetworks()) {
                android.net.NetworkCapabilities caps = cm.getNetworkCapabilities(network);
                if (caps == null
                        || !caps.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)
                        || caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN)) {
                    continue;
                }

                android.net.LinkProperties link = cm.getLinkProperties(network);
                String name = link != null ? link.getInterfaceName() : null;
                if (name == null || name.isEmpty()) {
                    continue;
                }

                int index = 0;
                try {
                    java.net.NetworkInterface ni = java.net.NetworkInterface.getByName(name);
                    if (ni != null) {
                        index = ni.getIndex();
                    }
                } catch (Throwable ignored) {
                }

                boolean metered = !caps.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_METERED);
                interfaceListener.updateDefaultInterface(name, index, metered, false);
                Log.i(TAG, "интерфейс по умолчанию: " + name + " (" + index + ")");
                return;
            }
        } catch (Throwable t) {
            Log.w(TAG, "refreshDefaultInterface: " + t.getMessage());
        }
    }
}
