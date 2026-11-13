namespace LinnworksDailySeed.RunLog;

public interface IRunLogStore
{
    DailyRunRecord? Load(DateOnly date);

    void Save(DailyRunRecord record);

    bool IsDayComplete(DailyRunRecord? record);

    RotationState LoadRotation();

    void SaveRotation(RotationState state);

    void Archive(DateOnly date, DailyRunRecord record);
}
