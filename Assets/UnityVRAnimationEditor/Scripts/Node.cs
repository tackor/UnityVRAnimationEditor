﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using VRTK;
using UnityEditor;
using System;
using System.Reflection;

public class Node : MonoBehaviour {

    public GenerateNodes generateNodes;
    public Transform followTarget ;
    BoxCollider targetCollider;
    Rigidbody targetRigidbody;
    public float colliderSize = 0.1f;
    VRTK.VRTK_InteractableObject interactableObject;

    MeshFilter meshFilter;
    public Mesh wireframeMesh;
    public Mesh normalMesh;

    Renderer renderer;

    [SerializeField]public Material normalMaterial;
    [SerializeField]public Material highlightedMaterial;

    public static bool moveConstraint = false;
    public static bool rotateConstraint = false;
    public static bool scaleConstraint = false;

    public static int undoID = 0;

    bool grabbing = false;

    public enum eNodeType
    {
        ROOT,
        IK,
        FK,
    }

    public eNodeType type = eNodeType.ROOT;

    bool isParent = false;//親のノードかどうか。一番親のノードから階層順にFollowTargetする
    Node[] childNodes; //子のノード
    Node parentNode; //親のノード

    Vector3 savedPosition;
    Quaternion savedQuaternion;
    Vector3 savedScale;

    static Type typeAnimationUtility;

    // Use this for initialization
    protected virtual void Start () {

        //一度だけキャッシュする
        if (typeAnimationUtility == null)
        {
            typeAnimationUtility = System.Reflection.Assembly.Load("UnityEditor.dll").GetType("UnityEditor.AnimationUtility");
        }

        meshFilter = GetComponent<MeshFilter>();
        renderer = GetComponent<Renderer>();
        renderer.material = normalMaterial;

        transform.localScale = new Vector3(colliderSize, colliderSize, colliderSize);
        if (followTarget == null) { return; }

        //既存Collider削除
        var colliders = followTarget.gameObject.GetComponents<Collider>();
        foreach (var collider in colliders)
        {
            DestroyImmediate(collider);
        }

        //既存Rigidbody削除
        var rigidBody = followTarget.gameObject.GetComponent<Rigidbody>();
        if (rigidBody)
        {
            DestroyImmediate(rigidBody);
        }

        //その他既存の余計な物削除
        var fixedJointGrabAttach = followTarget.GetComponent<VRTK.GrabAttachMechanics.VRTK_FixedJointGrabAttach>();
        if (fixedJointGrabAttach) { DestroyImmediate(fixedJointGrabAttach); }

        interactableObject = followTarget.GetComponent<VRTK.VRTK_InteractableObject>();
        if (interactableObject) { DestroyImmediate(interactableObject); }

        followTarget.gameObject.layer = LayerMask.NameToLayer("Node");

        targetCollider = followTarget.gameObject.AddComponent<BoxCollider>();
        targetCollider.center = Vector3.zero;
        fixedJointGrabAttach = followTarget.gameObject.AddComponent<VRTK.GrabAttachMechanics.VRTK_FixedJointGrabAttach>();
        fixedJointGrabAttach.precisionGrab = true;
        targetRigidbody = followTarget.gameObject.AddComponent<Rigidbody>();
        targetRigidbody.isKinematic = true;

        //targetにScalingNodeが付いてたらノードスケール変更
        var scalingNode = followTarget.GetComponent<ScalingNode>();
        if (scalingNode)
        {
            colliderSize *= scalingNode.scale;
        }

        /*if (type == eNodeType.FK)//FKNodeは移動できない
        {
            rigidBody.constraints = RigidbodyConstraints.FreezePosition;
        }*/

        interactableObject = followTarget.gameObject.AddComponent<VRTK.VRTK_InteractableObject>();
        interactableObject.isGrabbable = true;
        interactableObject.InteractableObjectGrabbed += new VRTK.InteractableObjectEventHandler(DoObjectGrab);
        interactableObject.InteractableObjectUngrabbed += new VRTK.InteractableObjectEventHandler(DoObjectUngrabbed);

        //親のNodeかどうか
        var parentTarget = followTarget.parent;
        if (parentTarget == null)
        {
            isParent = true;
        }
        else
        {
            if (generateNodes.transformToNodeDic.ContainsKey(parentTarget)) //親がNodeなら子
            {
                isParent = false;
                parentNode = generateNodes.transformToNodeDic[parentTarget];
            }
            else
            {
                isParent = true;
                parentNode = null;
            }
        }

        List<Node> childNodeList = new List<Node>();
        //子のNodeをすべて取得
        for (int i = 0; i < followTarget.childCount; i++)
        {
            var child = followTarget.GetChild(i);
            if (generateNodes.transformToNodeDic.ContainsKey(child))
            {
                childNodeList.Add(generateNodes.transformToNodeDic[child]);
            }
        }
        childNodes = childNodeList.ToArray();
    }

    private void InteractObjectHighlighterHighlighted(object o, InteractObjectHighlighterEventArgs e)
    {
        if (e.interactionType == VRTK_InteractableObject.InteractionType.Touch)
        {
            meshFilter.sharedMesh = normalMesh;
        }
    }

    private void InteractObjectHighlighterUnhighlighted(object o, InteractObjectHighlighterEventArgs e)
    {
        if (e.interactionType == VRTK_InteractableObject.InteractionType.Untouch)
        {
            meshFilter.sharedMesh = wireframeMesh;
        }
    }


    private void DoObjectGrab(object sender, VRTK.InteractableObjectEventArgs e)
    {
        //Debug.Log(e.interactingObject.name);

        //移動、回転、拡縮のconstraintをrigidbodyに適用
        RigidbodyConstraints constraint = RigidbodyConstraints.None;
        if (moveConstraint || type == eNodeType.FK)
        {
            constraint |= RigidbodyConstraints.FreezePosition;
        }
        if (rotateConstraint)
        {
            constraint |= RigidbodyConstraints.FreezeRotation;
        }
        targetRigidbody.constraints = constraint;
        //Debug.Log(targetRigidbody.name);

        //Grabする前のtransformを記録しておく
        savedPosition = followTarget.localPosition;
        savedQuaternion = followTarget.localRotation;
        savedScale = followTarget.localScale;

        if (generateNodes.RecordAllChildObjects)
        {
            generateNodes.RecordAllGrab();
        }


        grabbing = true;
    }

    private void DoObjectUngrabbed(object sender, VRTK.InteractableObjectEventArgs e)
    {
        string undoIDStr = "move target" + undoID.ToString();

        var tempPosition = followTarget.localPosition;
        var tempRotation = followTarget.localRotation;
        var tempScale = followTarget.localScale;

        //Grabする前の時点と比較
        followTarget.localPosition = savedPosition;
        followTarget.localRotation = savedQuaternion;
        followTarget.localScale = savedScale;
        

        //アングラブ時にアンドゥに記録する事でアニメーションウインドウで録画できる
        UnityEditor.Undo.RecordObject(followTarget, undoIDStr);
        //UnityEditor.Undo.RegisterCompleteObjectUndo(followTarget, undoIDStr);
        undoID++;

        
        followTarget.localPosition = tempPosition;
        followTarget.localRotation = tempRotation;
        followTarget.localScale = tempScale;
        
        UnityEditor.Undo.FlushUndoRecordObjects();
        

        grabbing = false;
    }

    private void RecordObject()
    {

        AnimationClip clip = wAnimationWindowHelper.GetAnimationWindowCurrentClip();
        if (clip == null) { return; }
        if (PlayManually.IsPlaying() && clip.length < wAnimationWindowHelper.GetCurrentTime()) { return; } //クリップが終了してたら

        Transform rootObjectTransform = wAnimationWindowHelper.GetAnimationWindowCurrentRootGameObject().transform;
        string path = AnimationRecorderHelper.GetTransformPathName(rootObjectTransform, followTarget.transform);

        //今編集中のアニメーターと無関係なオブジェクトならレコードしない
        if (path == null)
        {
            return;
        }

        float time;
        if (PlayManually.IsPlaying())
        {
            time = PlayManually.nowFrameTime;
        }
        else
        {
            time =wAnimationWindowHelper.GetCurrentTime();
        }

        SetCurve(time,clip, path, followTarget.transform);

        /*
        if (generateNodes.RecordAllChildObjects)
        {
            generateNodes.RecordAllUngrab();
        }*/

        //Grabする前のtransformを記録しておく
        /*savedPosition = followTarget.localPosition;
        savedQuaternion = followTarget.localRotation;
        savedScale = followTarget.localScale;*/
    }

    // Update is called once per frame
    protected virtual void Update () {

        if (followTarget == null) { return; }

        //ターゲットが非表示ならノードも非表示にする
        if (followTarget.gameObject.activeInHierarchy)
        {
            if (!renderer.enabled)
            {
                renderer.enabled = true;
            }
        }
        else
        {
            if (renderer.enabled)
            {
                renderer.enabled = false;
            }
        }

        if (isParent)
        {          
            UpdateFollow();
        }

    }

    private void LateUpdate()
    {
        if (grabbing)
        {
            UpdateGrabbing();

            RecordObject();
        }

    }

    protected virtual void UpdateGrabbing()
    {
        //親から実行する
        /*if (!isParent && parentNode != null)
        {
            parentNode.UpdateGrabbing();
        }*/

        followTarget.position = transform.position;
        followTarget.rotation = transform.rotation;
    }

    protected virtual void UpdateFollow()
    {
        var targetScale = followTarget.lossyScale;
        transform.localScale = targetScale * colliderSize;

        //targetのcolliderサイズが0.2になるように調整
        targetCollider.size = new Vector3(colliderSize , colliderSize , colliderSize );

        transform.position = followTarget.position;
        transform.rotation = followTarget.rotation;

        //親から順番にUpdateFollowする
        foreach (var child in childNodes)
        {
            child.UpdateFollow();
        }
    }

    int triggerCount = 0;

    private void OnTriggerEnter(Collider other)
    {
        if (other.name == "GrabSphere")
        {
            meshFilter.sharedMesh = normalMesh;
            renderer.material = highlightedMaterial;
        }

    }

    private void OnTriggerExit(Collider other)
    {
        if (other.name == "GrabSphere")
        {
            meshFilter.sharedMesh = wireframeMesh;
            renderer.material = normalMaterial;
        }

    }

    static void SetKey(AnimationCurve curve, float time, float value)
    {
        float prevTime = PlayManually.prevFrameTime;
        //Debug.Log("time:" + time);
        //Debug.Log("prevTime:" + prevTime);

        if (PlayManually.IsPlaying() && prevTime != time)//リアルタイムレコードなら
        {
            var keys = curve.keys;
            for (int i = keys.Length - 1; i >= 0; i--)
            {
                //ループしてたら
                if (prevTime > time)
                {
                    //前回以降のキーは削除
                    if (keys[i].time > prevTime)
                    {
                        curve.RemoveKey(i);
                    }

                    //timeより前のキーは削除
                    else if (keys[i].time <= time)
                    {
                        curve.RemoveKey(i);
                    }
                }
                else if (keys[i].time > prevTime && keys[i].time <= time)
                {
                    //Debug.Log("RemoveKey:" + i + "time:" + keys[i].time);
                    //前回から今回までのキーは削除
                    curve.RemoveKey(i);
                }
            }
        }
        else//再生中じゃないなら
        {
            var keys = curve.keys;
            for (int i = keys.Length - 1; i >= 0; i--)
            {
                if (keys[i].time == time)
                {
                    //同じフレームのキーは削除
                    curve.RemoveKey(i);
                }
            }
        }

        //Debug.Log("AddKey time:" + time);
        curve.AddKey(time, value);
    }

    static void SetEditorCurveNoSync(AnimationClip clip, EditorCurveBinding binding, AnimationCurve curve)
    {       
        typeAnimationUtility.InvokeMember("SetEditorCurveNoSync", System.Reflection.BindingFlags.InvokeMethod | BindingFlags.NonPublic | BindingFlags.Static, null, null, new object[] { clip, binding, curve });
    }

    public static void SetCurve(float time,AnimationClip clip,  string path, Transform t)
    {

        Quaternion rot = t.localRotation;
        Vector3 eulerAngles = rot.eulerAngles;

        {
            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.path = path;
            curveBinding.propertyName = "m_LocalRotation.x";
            curveBinding.type = typeof(Transform);
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            if (curve == null)
            {
                curve = new AnimationCurve();
            }

            SetKey(curve, time, rot.x);
            SetEditorCurveNoSync(clip, curveBinding, curve);
            //clip.SetCurve(path, typeof(Transform), "localRotation.x", curve);
        }

        {
            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.path = path;
            curveBinding.propertyName = "m_LocalRotation.y";
            curveBinding.type = typeof(Transform);
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            if (curve == null)
            {
                curve = new AnimationCurve();
            }
            SetKey(curve, time, rot.y);
            SetEditorCurveNoSync(clip, curveBinding, curve);
            //clip.SetCurve(path, typeof(Transform), "localRotation.y", curve);
        }

        {
            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.path = path;
            curveBinding.propertyName = "m_LocalRotation.z";
            curveBinding.type = typeof(Transform);
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            if (curve == null)
            {
                curve = new AnimationCurve();
            }
            SetKey(curve, time, rot.z);
            SetEditorCurveNoSync(clip, curveBinding, curve);
            //clip.SetCurve(path, typeof(Transform), "localRotation.z", curve);
        }

        {
            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.path = path;
            curveBinding.propertyName = "m_LocalRotation.w";
            curveBinding.type = typeof(Transform);
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            if (curve == null)
            {
                curve = new AnimationCurve();
            }
            SetKey(curve, time, rot.w);
            SetEditorCurveNoSync(clip, curveBinding, curve);
            //clip.SetCurve(path, typeof(Transform), "localRotation.w", curve);
        }

        Vector3 pos = t.localPosition;

        {
            //clip.SetCurve(path, typeof(Transform), "localPosition.x", curve);
            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.path = path;
            curveBinding.propertyName = "m_LocalPosition.x";
            curveBinding.type = typeof(Transform);
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            if (curve == null)
            {
                curve = new AnimationCurve();
            }
            SetKey(curve, time, pos.x);
            SetEditorCurveNoSync(clip, curveBinding, curve);
        }

        {
            //clip.SetCurve(path, typeof(Transform), "localPosition.y", curve);
            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.path = path;
            curveBinding.propertyName = "m_LocalPosition.y";
            curveBinding.type = typeof(Transform);
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            if (curve == null)
            {
                curve = new AnimationCurve();
            }
            SetKey(curve, time, pos.y);
            SetEditorCurveNoSync(clip, curveBinding, curve);
        }

        {
            //clip.SetCurve(path, typeof(Transform), "localPosition.z", curve);
            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.path = path;
            curveBinding.propertyName = "m_LocalPosition.z";
            curveBinding.type = typeof(Transform);
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            if (curve == null)
            {
                curve = new AnimationCurve();
            }
            SetKey(curve, time, pos.z);
            SetEditorCurveNoSync(clip, curveBinding, curve);
        }

        /*
        Vector3 scale = t.localScale;

        {
            //clip.SetCurve(path, typeof(Transform), "localScale.x", curve);
            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.path = path;
            curveBinding.propertyName = "m_LocalScale.x";
            curveBinding.type = typeof(Transform);
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            if (curve == null)
            {
                curve = new AnimationCurve();
            }
            SetKey(curve, time, scale.x);
            AnimationUtility.SetEditorCurve(clip, curveBinding, curve);
        }

        {
            //clip.SetCurve(path, typeof(Transform), "localScale.y", curve);
            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.path = path;
            curveBinding.propertyName = "m_LocalScale.y";
            curveBinding.type = typeof(Transform);
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            if (curve == null)
            {
                curve = new AnimationCurve();
            }
            SetKey(curve, time, scale.y);
            AnimationUtility.SetEditorCurve(clip, curveBinding, curve);
        }

        {
            //clip.SetCurve(path, typeof(Transform), "localScale.z", curve);
            EditorCurveBinding curveBinding = new EditorCurveBinding();
            curveBinding.path = path;
            curveBinding.propertyName = "m_LocalScale.z";
            curveBinding.type = typeof(Transform);
            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, curveBinding);
            if (curve == null)
            {
                curve = new AnimationCurve();
            }
            SetKey(curve, time, scale.z);
            AnimationUtility.SetEditorCurve(clip, curveBinding, curve);
        }*/
    }
}
