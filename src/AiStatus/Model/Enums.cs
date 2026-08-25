namespace AiStatus.Model;

public enum HealthState { Ok, Degraded, AuthExpired, Unreachable, Disabled }
public enum Severity { Normal, Warning, Critical }
public enum AlertKind { Warning, Critical, LimitReached, AuthExpired }
