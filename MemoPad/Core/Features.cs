namespace MemoPad.Core;

/// <summary>
/// 기능 스위치. 네트워크 기능(포트를 쓰는 것: 보내기 · 답장 · 전달 · 회람 · 사용자 상태 확인 · 부재중 · 대화 · 주소록)은 꺼 둠:
/// 포트를 열지 않고, 메뉴 · 트레이 · 단축키 · 설정에서도 보이지 않음.
/// 코드는 남겨 두어 다시 켤 수 있음 (MemoPad.Checks 는 켜고 시험해 코드가 깨지지 않게 함)
/// </summary>
public static class Features
{
    public static bool Network { get; internal set; }
}
