package com.vienlink.platform;

import android.util.Log;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

import libbox.ExchangeContext;
import libbox.LocalDNSTransport;

/** Локальный DNS для ядра (только Java). */
public final class VieNLinkDnsTransport implements LocalDNSTransport {
    private static final String TAG = "VieNLink";

    // 1.1.1.1 у части российских операторов режется; основной — Яндекс DNS.
    private static final String[] UPSTREAMS = { "77.88.8.8", "1.1.1.1" };
    private static final int UPSTREAM_PORT = 53;

    private final ExecutorService pool = Executors.newCachedThreadPool(r -> {
        Thread thread = new Thread(r, "vienlink-dns");
        thread.setDaemon(true);
        return thread;
    });

    @Override
    public boolean raw() {
        return true;
    }

    @Override
    public void lookup(ExchangeContext context, String network, String host) {
        if (context == null || host == null || host.isEmpty()) {
            return;
        }

        boolean preferIpv4 = "ip4".equalsIgnoreCase(network);
        pool.execute(() -> {
            try {
                InetAddress[] addresses = InetAddress.getAllByName(host);
                InetAddress chosen = null;
                for (InetAddress address : addresses) {
                    if (preferIpv4 && !(address instanceof Inet4Address)) {
                        continue;
                    }
                    chosen = address;
                    break;
                }
                if (chosen == null && addresses.length > 0) {
                    chosen = addresses[0];
                }
                if (chosen == null) {
                    context.errorCode(2);
                } else {
                    context.success(chosen.getHostAddress());
                }
            } catch (Throwable t) {
                Log.w(TAG, "DNS lookup " + host + ": " + t.getMessage());
                context.errorCode(1);
            }
        });
    }

    @Override
    public void exchange(ExchangeContext context, byte[] message) {
        if (context == null || message == null || message.length == 0) {
            return;
        }

        pool.execute(() -> {
            for (String upstream : UPSTREAMS) {
                try (DatagramSocket socket = new DatagramSocket()) {
                    VieNLinkJavaBridge.protectDatagram(socket);
                    socket.setSoTimeout(3000);
                    InetAddress server = InetAddress.getByName(upstream);
                    socket.send(new DatagramPacket(message, message.length, server, UPSTREAM_PORT));

                    byte[] buffer = new byte[4096];
                    DatagramPacket answer = new DatagramPacket(buffer, buffer.length);
                    socket.receive(answer);

                    byte[] data = new byte[answer.getLength()];
                    System.arraycopy(answer.getData(), answer.getOffset(), data, 0, answer.getLength());
                    context.rawSuccess(data);
                    return;
                } catch (Throwable t) {
                    Log.w(TAG, "DNS " + upstream + ": " + t.getMessage());
                }
            }

            context.errorCode(110);
        });
    }
}
