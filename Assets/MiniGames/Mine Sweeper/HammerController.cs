using DG.Tweening;
using UnityEngine;

public class HammerController : MonoBehaviour {
    private float _timer;
    private bool _isAttacking;

    [SerializeField] private Transform hammerContainer;
    [SerializeField] private Vector3 targetAngle = Vector3.zero;
    [SerializeField] private float duration = 0.5f;
    [SerializeField] private Ease ease = Ease.InSine;
    [SerializeField] private int loops = 2;
    [SerializeField] private LoopType loopType = LoopType.Yoyo;

    private void Update() {
        if (!_isAttacking && Input.GetMouseButtonDown(0)) {
            hammerContainer.DOLocalRotate(targetAngle, duration).SetEase(Ease.InSine).SetLoops(loops, LoopType.Yoyo).onComplete += () => { _isAttacking = false; };
            _timer = Time.time + duration;
            _isAttacking = true;
        }
        _timer -= Time.deltaTime;
    }
}
