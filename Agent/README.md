Watcher V6 Windows Agent installer includes Install / Update and Uninstall Agent buttons. See ../README.md for instructions.

TCP and UDP socket monitoring: see [UDP.md](UDP.md) for available fields, limitations and upgrade order.

For the device overview, the agent now queues a full TCP/UDP snapshot on startup and on every heartbeat (normally once a minute). A completion heartbeat declares the expected row count; partial deliveries are not displayed as complete. Upgrade the Collector before installing this agent, because `TCP_SNAPSHOT` and `UDP_SNAPSHOT` are new event types. Existing open/close monitoring and ACK-based retries are retained. Snapshot records increase stored event/CSV volume.
