package com.vienlink.platform;

import android.util.Log;

import libbox.CommandServerHandler;
import libbox.SystemProxyStatus;

/** Обработчик команд ядра. */
public final class VieNLinkServerHandler implements CommandServerHandler {
    private static final String TAG = "VieNLink";

    @Override
    public int connectSSHAgent() {
        return -1;
    }

    @Override
    public SystemProxyStatus getSystemProxyStatus() {
        return null;
    }

    @Override
    public void serviceReload() {
        Log.i(TAG, "ядро: ServiceReload");
    }

    @Override
    public void serviceStop() {
        Log.i(TAG, "ядро: ServiceStop");
    }

    @Override
    public void setSystemProxyEnabled(boolean enabled) {
    }

    @Override
    public void triggerNativeCrash() {
    }

    @Override
    public void writeDebugMessage(String message) {
        if (message != null && !message.isEmpty()) {
            Log.d(TAG, message);
        }
    }
}
