// The packet and the dash: the same offsets, rules and state file as the Redline Deck Stream Deck plugin, which is tested
// to write the same state.json, field for field, for the same packets. docs/STATE-FILE.md describes the file.
using System.Buffers.Binary;
using System.Text.Json;

namespace RedlineLink;

/// <summary>One Forza Horizon 6 Data Out packet: the fields the dashboard file uses, at the documented offsets.</summary>
public sealed class Packet
{
    public const int Size = 324;
    public int IsRaceOn, CarOrdinal, CarClass, Pi, Drivetrain, Cylinders;
    public uint CarGroup;
    public float MaxRpm, IdleRpm, Rpm, AccX, AccY, AccZ, PosX, PosY, PosZ, Speed, Power, Torque, Boost, Fuel, Distance, BestLap, LastLap, CurrentLap, RaceTime;
    public float[] Susp = new float[4], Slip = new float[4], TempF = new float[4];
    public int LapNumber, Position, Accel, Brake, Clutch, HandBrake, Gear, Steer;

    static float F(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadSingleLittleEndian(b.Slice(o, 4));
    static int I(ReadOnlySpan<byte> b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.Slice(o, 4));

    /// <summary>A packet, or null when it is not 324 bytes (Forza Motorsport's formats and anything else are refused).</summary>
    public static Packet? Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length != Size) return null;
        var p = new Packet
        {
            IsRaceOn = I(b, 0), MaxRpm = F(b, 8), IdleRpm = F(b, 12), Rpm = F(b, 16), AccX = F(b, 20), AccY = F(b, 24), AccZ = F(b, 28),
            CarOrdinal = I(b, 212), CarClass = I(b, 216), Pi = I(b, 220), Drivetrain = I(b, 224), Cylinders = I(b, 228),
            CarGroup = BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(232, 4)),
            PosX = F(b, 244), PosY = F(b, 248), PosZ = F(b, 252), Speed = F(b, 256), Power = F(b, 260), Torque = F(b, 264),
            Boost = F(b, 284), Fuel = F(b, 288), Distance = F(b, 292), BestLap = F(b, 296), LastLap = F(b, 300), CurrentLap = F(b, 304), RaceTime = F(b, 308),
            LapNumber = BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(312, 2)), Position = b[314], Accel = b[315], Brake = b[316], Clutch = b[317],
            HandBrake = b[318], Gear = b[319], Steer = (sbyte)b[320],
        };
        for (int i = 0; i < 4; i++) { p.Susp[i] = F(b, 68 + 4 * i); p.Slip[i] = F(b, 180 + 4 * i); p.TempF[i] = F(b, 268 + 4 * i); }
        return p;
    }
}

/// <summary>What the dash keeps: the state, the gear rule, this run's peaks, the session's best lap, the last car.</summary>
public sealed class Dash
{
    public const long StaleMs = 1000, GearHoldMs = 600;
    const double G = 9.80665;
    static readonly string[] Classes = { "D", "C", "B", "A", "S1", "S2", "R", "X" };
    static readonly string[] Drives = { "FWD", "RWD", "AWD" };
    public Packet? P; long at, gearAt; string? gear; double topSpeed, peakLat, peakLon, peakTot; double? bestLap; Packet? car, lastRun; string? runCar;
    public long Packets;

    static string? GearText(int g) => g == 0 ? "R" : g >= 1 && g <= 10 ? g.ToString() : null;
    static bool Ok(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

    public void ResetPeaks() { topSpeed = 0; peakLat = peakLon = peakTot = 0; }

    public void Push(Packet p, long now)
    {
        P = p; at = now; Packets++;
        var g = GearText(p.Gear);
        if (g != null) { gear = g; gearAt = now; }
        if (p.IsRaceOn != 1) return;
        lastRun = p;
        var key = $"{p.CarOrdinal}:{p.Pi}";
        if (key != runCar) { runCar = key; ResetPeaks(); }
        if (Ok(p.Speed) && p.Speed > topSpeed && p.Speed < 200) topSpeed = p.Speed;
        double lat = Math.Abs(p.AccX) / G, lon = Math.Abs(p.AccZ) / G, tot = Math.Sqrt(lat * lat + lon * lon);
        if (Ok(lat) && lat < 6) peakLat = Math.Max(peakLat, lat);
        if (Ok(lon) && lon < 6) peakLon = Math.Max(peakLon, lon);
        if (Ok(tot) && tot < 6) peakTot = Math.Max(peakTot, tot);
        foreach (var t in new[] { p.BestLap, p.LastLap }) if (Ok(t) && t > 0 && (bestLap == null || t < bestLap)) bestLap = t;
        if (p.CarOrdinal > 0) car = p;
    }

    public string State(long now) => P == null ? "waiting" : (now - at > StaleMs || P.IsRaceOn != 1) ? "paused" : "driving";
    public bool Race => P != null && P.IsRaceOn == 1 && (P.Position > 0 || P.LapNumber > 0);

    static double? R1(double v) => Ok(v) ? Math.Round(v * 10) / 10 : null;
    static double? R3(double v) => Ok(v) ? Math.Round(v * 1000) / 1000 : null;
    static void Num(Utf8JsonWriter w, string k, double? v) { if (v.HasValue) w.WriteNumber(k, v.Value); else w.WriteNull(k); }
    static void Arr(Utf8JsonWriter w, string k, float[] a, Func<double, double?> r) { w.WriteStartArray(k); foreach (var x in a) { var v = r(x); if (v.HasValue) w.WriteNumberValue(v.Value); else w.WriteNullValue(); } w.WriteEndArray(); }

    /// <summary>state.json, in the schema docs/STATE-FILE.md describes (the same file the Redline Deck plugin writes).</summary>
    public byte[] StateJson(long now, int port, int pps, string linkState, string writer = "companion")
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            var st = linkState is "busy" or "error" ? linkState : State(now);
            w.WriteStartObject();
            w.WriteString("app", "Redline Deck"); w.WriteNumber("v", 1); w.WriteString("writer", writer); w.WriteNumber("at", now); w.WriteString("state", st);
            w.WriteStartObject("link"); w.WriteNumber("port", port); w.WriteNumber("pps", pps); w.WriteNumber("packets", Packets); w.WriteEndObject();
            w.WriteStartObject("units"); w.WriteString("speed", "kmh"); w.WriteString("temp", "C"); w.WriteString("power", "hp"); w.WriteString("torque", "Nm"); w.WriteString("boost", "psi"); w.WriteEndObject();
            if (car == null) w.WriteNull("car");
            else
            {
                w.WriteStartObject("car");
                w.WriteNumber("ordinal", car.CarOrdinal);
                if (car.CarClass >= 0 && car.CarClass < 8) w.WriteString("cls", Classes[car.CarClass]); else w.WriteNull("cls");
                var pc = car.Pi < 100 ? null : car.Pi <= 500 ? "D" : car.Pi <= 600 ? "C" : car.Pi <= 700 ? "B" : car.Pi <= 800 ? "A" : car.Pi <= 900 ? "S1" : car.Pi <= 998 ? "S2" : "X";
                if (pc != null) w.WriteString("piClass", pc); else w.WriteNull("piClass");
                if (car.Pi != 0) w.WriteNumber("pi", car.Pi); else w.WriteNull("pi");
                if (car.Drivetrain >= 0 && car.Drivetrain < 3) w.WriteString("drive", Drives[car.Drivetrain]); else w.WriteNull("drive");
                if (car.Cylinders != 0) w.WriteNumber("cylinders", car.Cylinders); else w.WriteNull("cylinders");
                if (car.CarGroup != 0) w.WriteNumber("group", car.CarGroup); else w.WriteNull("group");
                w.WriteEndObject();
            }
            w.WriteStartObject("session");
            Num(w, "topSpeed", topSpeed > 0 ? R1(topSpeed) : null);
            w.WriteStartObject("peakG"); Num(w, "lat", R3(peakLat)); Num(w, "lon", R3(peakLon)); Num(w, "total", R3(peakTot)); w.WriteEndObject();
            Num(w, "bestLap", bestLap.HasValue ? R3(bestLap.Value) : null);
            w.WriteEndObject();
            if (P != null && State(now) == "driving")
            {
                var p = P;
                var g = GearText(p.Gear) ?? (now - gearAt <= GearHoldMs && gear != null ? gear : "N");
                Num(w, "speed", R1(p.Speed)); w.WriteString("gear", g);
                w.WriteStartObject("rpm"); w.WriteNumber("now", Math.Round(p.Rpm)); w.WriteNumber("max", Math.Round(p.MaxRpm)); w.WriteNumber("idle", Math.Round(p.IdleRpm));
                Num(w, "frac", p.MaxRpm > 0 ? R3(Math.Clamp(p.Rpm / p.MaxRpm, 0, 1)) : null); w.WriteEndObject();
                Num(w, "boost", R1(p.Boost)); w.WriteNumber("power", Math.Round(p.Power)); Num(w, "torque", R1(p.Torque)); Num(w, "fuel", R3(Math.Clamp(p.Fuel, 0, 1)));
                w.WriteStartObject("race");
                w.WriteBoolean("on", Race);
                if (p.Position > 0) w.WriteNumber("position", p.Position); else w.WriteNull("position");
                w.WriteNumber("lap", p.LapNumber + 1); w.WriteNumber("laps", p.LapNumber);
                Num(w, "current", p.CurrentLap > 0 ? R3(p.CurrentLap) : null); Num(w, "last", p.LastLap > 0 ? R3(p.LastLap) : null);
                Num(w, "best", p.BestLap > 0 ? R3(p.BestLap) : null); Num(w, "time", p.RaceTime > 0 ? R3(p.RaceTime) : null);
                w.WriteEndObject();
                w.WriteNumber("distance", Math.Round(p.Distance));
                w.WriteStartObject("tyres"); Arr(w, "tempF", p.TempF, R1); Arr(w, "slip", p.Slip, R3); Arr(w, "travel", p.Susp, R3); w.WriteEndObject();
                w.WriteStartObject("g"); Num(w, "lat", R3(p.AccX / G)); Num(w, "lon", R3(p.AccZ / G)); Num(w, "vert", R3(p.AccY / G)); w.WriteEndObject();
                w.WriteStartObject("inputs"); Num(w, "throttle", R3(p.Accel / 255.0)); Num(w, "brake", R3(p.Brake / 255.0)); Num(w, "clutch", R3(p.Clutch / 255.0));
                Num(w, "handbrake", R3(p.HandBrake / 255.0)); Num(w, "steer", R3(Math.Clamp(p.Steer / 127.0, -1, 1))); w.WriteEndObject();
                w.WriteStartObject("pos"); Num(w, "x", R1(p.PosX)); Num(w, "y", R1(p.PosY)); Num(w, "z", R1(p.PosZ)); w.WriteEndObject();
                bool air = Math.Abs(p.Speed) > 3; foreach (var s in p.Susp) if (!(Ok(s) && s <= 0.02)) air = false;
                w.WriteBoolean("airborne", air);
            }
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}
