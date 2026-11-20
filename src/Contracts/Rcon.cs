namespace Contracts;

// Phase 7a: BattlEye RCon between the panel and the agent.
public enum RconState { Off, Connecting, Connected, Failed }

// What the agent writes into the profile's BattlEye\BEServer_x64.cfg at launch (bound to 127.0.0.1). The password never leaves the
// panel→agent channel: it isn't logged, audited or sent to browsers.
public sealed record RconSpec(int Port, string Password);

public sealed record RconReply(bool Ok, string? Text, string? Error);
