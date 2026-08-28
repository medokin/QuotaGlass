namespace QuotaGlass.Model;

public enum HealthState { Ok, Degraded, AuthExpired, Unreachable }
public enum Severity { Normal, Warning, Critical }
public enum AlertKind { Warning, Critical, LimitReached, AuthExpired }
