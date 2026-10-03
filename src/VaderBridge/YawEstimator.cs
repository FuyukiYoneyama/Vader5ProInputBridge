using System.Diagnostics;

internal sealed class YawEstimator
{
    internal sealed record State(double AngleDeg, double RateDegPerSecond, double BiasDegPerSecond,
        double CorrectedRateDegPerSecond, double DtSeconds, bool BiasReady, int BiasSamples,
        double BiasDurationSeconds, double BiasStdDevDegPerSecond, double RangeDeg,
        bool Recentered, double HomePressMilliseconds, double SkippedGapSeconds, bool Stationary);

    private const double RadToDeg = 180 / Math.PI;
    private readonly YawConfig _config;
    private readonly int _sensorIndex;
    private readonly double _angleLimit;
    private long _lastTimestamp, _biasStarted, _homePressed;
    private double _angle, _bias, _biasMean, _biasM2;
    private int _biasSamples;
    private bool _biasReady, _homeInitialized, _homeDown, _suppressHome, _stationary;
    private float[]? _previousAccel;
    internal State Snapshot { get; private set; }

    internal YawEstimator(YawConfig config, double angleLimit = double.PositiveInfinity)
    {
        _config = config;
        if (double.IsNaN(angleLimit) || angleLimit <= 0)
            throw new InvalidDataException("Yaw積算の上限には正の値を指定してください。");
        _angleLimit = angleLimit;
        _sensorIndex = config.SensorAxis.Trim().ToUpperInvariant() switch
        {
            "X" => 0, "Y" => 1, "Z" => 2,
            _ => throw new InvalidDataException("yaw.sensorAxisにはX/Y/Zを指定してください。")
        };
        foreach (double value in new[] { config.YawRangeDeg, config.BiasCalibrationSeconds,
            config.StationaryMaxRateDegPerSecond, config.StationaryStdDevDegPerSecond,
            config.StationaryGravityTolerance, config.StationaryAccelChangeTolerance,
            config.HomeShortPressMilliseconds, config.MaxSampleGapSeconds })
            if (!double.IsFinite(value) || value <= 0)
                throw new InvalidDataException("Yawの範囲・時間・静止判定幅には有限の正の値を指定してください。");
        if (config.MinimumBiasSamples < 2)
            throw new InvalidDataException("yaw.minimumBiasSamplesには2以上を指定してください。");
        Snapshot = Current(0, 0, 0, false, 0, 0, 0);
    }

    // 読取りの再開では時計と観測途中のHOME押下を更新し、角度と確定済み偏差を保持する。
    internal void ResumeClock()
    {
        _lastTimestamp = 0;
        _homeInitialized = false;
        _homePressed = 0;
        _previousAccel = null;
        _stationary = false;
        if (!_biasReady) ClearBiasWindow();
        Snapshot = Snapshot with { DtSeconds = 0, Recentered = false, HomePressMilliseconds = 0, SkippedGapSeconds = 0,
            Stationary = false, BiasSamples = _biasSamples, BiasDurationSeconds = _biasReady ? Snapshot.BiasDurationSeconds : 0,
            BiasStdDevDegPerSecond = _biasReady ? Snapshot.BiasStdDevDegPerSecond : 0 };
    }

    internal State Update(float[] gyro, float[] accel, bool homeDown, long timestamp)
    {
        double rawRate = gyro[_sensorIndex] * RadToDeg;
        double dt = _lastTimestamp == 0 ? 0 : (timestamp - _lastTimestamp) / (double)Stopwatch.Frequency;
        double skippedGap = dt > _config.MaxSampleGapSeconds ? dt : 0;
        if (skippedGap > 0 || dt < 0)
        {
            ResumeClock();
            dt = 0;
        }
        _lastTimestamp = timestamp;

        double duration = Snapshot.BiasDurationSeconds;
        double deviation = Snapshot.BiasStdDevDegPerSecond;
        double correctedRate = (rawRate - _bias) * (_config.Invert ? -1 : 1);
        if (!_biasReady)
        {
            bool stationary = gyro.All(value => float.IsFinite(value) && Math.Abs(value * RadToDeg) <= _config.StationaryMaxRateDegPerSecond)
                && accel.All(float.IsFinite);
            double gravity = Math.Sqrt(accel.Sum(value => (double)value * value));
            stationary &= Math.Abs(gravity - 9.80665) <= _config.StationaryGravityTolerance;
            if (_previousAccel is { } previous)
            {
                double change = Math.Sqrt(Enumerable.Range(0, 3).Sum(i => Math.Pow(accel[i] - previous[i], 2)));
                stationary &= change <= _config.StationaryAccelChangeTolerance;
            }
            _stationary = stationary;
            if (stationary)
            {
                if (_biasSamples == 0) _biasStarted = timestamp;
                _biasSamples++;
                double delta = rawRate - _biasMean;
                _biasMean += delta / _biasSamples;
                _biasM2 += delta * (rawRate - _biasMean);
                duration = (timestamp - _biasStarted) / (double)Stopwatch.Frequency;
                deviation = _biasSamples > 1 ? Math.Sqrt(Math.Max(0, _biasM2 / (_biasSamples - 1))) : 0;
                if (duration >= _config.BiasCalibrationSeconds && _biasSamples >= _config.MinimumBiasSamples)
                {
                    if (deviation <= _config.StationaryStdDevDegPerSecond)
                    {
                        _bias = _biasMean;
                        _biasReady = true;
                        correctedRate = (rawRate - _bias) * (_config.Invert ? -1 : 1);
                        dt = 0;
                    }
                    else { ClearBiasWindow(); duration = deviation = 0; }
                }
            }
            else { ClearBiasWindow(); duration = deviation = 0; }
        }
        // 偏差採集中も暫定偏差で角度を送り、確定後は測定した偏差を使う。
        // 上限の外側への積算を抑え、逆方向の回転を次の入力から反映する。
        _angle = Math.Clamp(_angle + correctedRate * dt, -_angleLimit, _angleLimit);
        _previousAccel = (float[])accel.Clone();

        bool recentered = false;
        double homeMilliseconds = 0;
        if (!_homeInitialized)
        {
            _homeInitialized = true;
            _homeDown = homeDown;
            _suppressHome = homeDown;
        }
        else if (homeDown && !_homeDown)
        {
            _homePressed = timestamp;
            _suppressHome = false;
        }
        else if (!homeDown && _homeDown)
        {
            if (!_suppressHome && _homePressed != 0)
            {
                homeMilliseconds = (timestamp - _homePressed) * 1000d / Stopwatch.Frequency;
                if (homeMilliseconds > 0 && homeMilliseconds < _config.HomeShortPressMilliseconds)
                {
                    _angle = 0;
                    recentered = true;
                }
            }
            _homePressed = 0;
            _suppressHome = false;
        }
        _homeDown = homeDown;
        Snapshot = Current(rawRate, correctedRate, dt, recentered, homeMilliseconds, skippedGap, duration, deviation);
        return Snapshot;
    }

    private State Current(double rate, double corrected, double dt, bool recentered, double homeMilliseconds,
        double skippedGap, double duration, double deviation = 0) =>
        new(_angle, rate, _bias, corrected, dt, _biasReady, _biasSamples, duration, deviation,
            double.IsFinite(_angleLimit) ? _angleLimit : _config.YawRangeDeg, recentered, homeMilliseconds, skippedGap, _stationary);

    private void ClearBiasWindow()
    {
        _biasStarted = 0;
        _biasSamples = 0;
        _biasMean = _biasM2 = 0;
    }
}
