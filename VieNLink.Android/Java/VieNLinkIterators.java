package com.vienlink.platform;

import java.util.List;

import libbox.NetworkInterfaceIterator;
import libbox.StringIterator;

/** Простые итераторы для libbox. */
final class VieNLinkIterators {
    private VieNLinkIterators() {
    }

    static final class NetworkInterfaces implements NetworkInterfaceIterator {
        private final List<libbox.NetworkInterface> items;
        private int position = -1;

        NetworkInterfaces(List<libbox.NetworkInterface> items) {
            this.items = items;
        }

        @Override
        public boolean hasNext() {
            return position + 1 < items.size();
        }

        @Override
        public libbox.NetworkInterface next() {
            position++;
            return position < items.size() ? items.get(position) : null;
        }
    }

    static final class Strings implements StringIterator {
        private final List<String> items;
        private int position = -1;

        Strings(List<String> items) {
            this.items = items;
        }

        @Override
        public boolean hasNext() {
            return position + 1 < items.size();
        }

        @Override
        public int len() {
            return items.size();
        }

        @Override
        public String next() {
            position++;
            return position < items.size() ? items.get(position) : null;
        }
    }
}
