package com.vienlink.platform;

import libbox.BridgeOptions;
import libbox.BridgeSession;
import libbox.ConnectionOwner;
import libbox.InterfaceUpdateListener;
import libbox.LocalDNSTransport;
import libbox.NeighborUpdateListener;
import libbox.NetworkInterfaceIterator;
import libbox.PlatformInterface;
import libbox.PlatformUser;
import libbox.ShellSession;
import libbox.StringIterator;
import libbox.TunOptions;
import libbox.WIFIState;

/**
 * PlatformInterface на Java: горячие вызовы ядра не входят в C#/MonoVM.
 */
public final class VieNLinkJavaPlatform implements PlatformInterface {
    private VieNLinkDnsTransport dns;

    @Override
    public void autoDetectInterfaceControl(int fd) {
        VieNLinkJavaBridge.protect(fd);
    }

    @Override
    public int openTun(TunOptions options) {
        return VieNLinkJavaBridge.openTun(options);
    }

    @Override
    public ConnectionOwner findConnectionOwner(int ipProtocol, String sourceAddress, int sourcePort,
                                               String destinationAddress, int destinationPort) {
        ConnectionOwner owner = new ConnectionOwner();
        owner.setUserId(android.os.Process.myUid());
        owner.setUserName("");
        owner.setProcessPath("unknown");
        return owner;
    }

    @Override
    public NetworkInterfaceIterator getInterfaces() {
        return VieNLinkJavaBridge.getInterfaces();
    }

    @Override
    public LocalDNSTransport localDNSTransport() {
        if (dns == null) {
            dns = new VieNLinkDnsTransport();
        }
        return dns;
    }

    @Override
    public void startDefaultInterfaceMonitor(InterfaceUpdateListener listener) {
        VieNLinkJavaBridge.startDefaultInterfaceMonitor(listener);
    }

    @Override
    public void closeDefaultInterfaceMonitor(InterfaceUpdateListener listener) {
        VieNLinkJavaBridge.stopInterfaceMonitor();
    }

    @Override
    public void clearDNSCache() {
    }

    @Override
    public void cancelNotification(String tag, int id) {
    }

    @Override
    public void checkPlatformShell() {
    }

    @Override
    public void closeNeighborMonitor(NeighborUpdateListener listener) {
    }

    @Override
    public BridgeSession createBridge(BridgeOptions options) {
        return null;
    }

    @Override
    public boolean includeAllNetworks() {
        return false;
    }

    @Override
    public String lookupSFTPServer() {
        return null;
    }

    @Override
    public PlatformUser lookupUser(String username) {
        return null;
    }

    @Override
    public ShellSession openShellSession(PlatformUser user, String command, StringIterator args,
                                         String terminal, int width, int height) {
        return null;
    }

    @Override
    public String readSystemSSHHostKey() {
        return null;
    }

    @Override
    public WIFIState readWIFIState() {
        return null;
    }

    @Override
    public void registerMyInterface(String name) {
    }

    @Override
    public void sendNotification(libbox.Notification notification) {
    }

    @Override
    public void startNeighborMonitor(NeighborUpdateListener listener) {
    }

    @Override
    public String tailscaleHostname() {
        return null;
    }

    @Override
    public boolean underNetworkExtension() {
        return false;
    }

    @Override
    public boolean usePlatformAutoDetectInterfaceControl() {
        return true;
    }

    @Override
    public boolean usePlatformBridge() {
        return false;
    }

    @Override
    public boolean usePlatformShell() {
        return false;
    }
}
