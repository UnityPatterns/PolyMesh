using UnityEngine;
using UnityEditor;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

[CustomEditor(typeof(PolyMesh))]
public class PolyMeshEditor : Editor
{
	enum State { Hover, Drag, BoxSelect, DragSelected, RotateSelected, ScaleSelected, Extrude }

	const float clickRadius = 0.12f;

	FieldInfo undoCallback;
	bool editing;
	bool tabDown;
	State state;

	List<Vector3> keyPoints;
	List<Vector3> curvePoints;
	List<bool> isCurve;

	Matrix4x4 worldToLocal;
	Quaternion inverseRotation;
	
	Vector3 mousePosition;
	Vector3 clickPosition;
	Vector3 screenMousePosition;
	MouseCursor mouseCursor = MouseCursor.Arrow;
	float snap;

	int dragIndex;
	List<int> selectedIndices = new List<int>();
	int nearestLine;
	Vector3 splitPosition;
	bool extrudeKeyDown;
	bool doExtrudeUpdate;
	bool draggingCurve;

	#region Inspector GUI

	public override void OnInspectorGUI()
	{
		if (target == null)
			return;

		if (polyMesh.keyPoints.Count == 0)
			CreateSquare(polyMesh, 0.5f);

		//Toggle editing mode
		if (editing)
		{
			if (GUILayout.Button("Stop Editing"))
			{
				editing = false;
				HideWireframe(false);
			}
		}
		else if (GUILayout.Button("Edit PolyMesh"))
		{
			editing = true;
			HideWireframe(hideWireframe);
		}

		//UV settings
		if (uvSettings = EditorGUILayout.Foldout(uvSettings, "UVs"))
		{
			var uvPosition = EditorGUILayout.Vector2Field("Position", polyMesh.uvPosition);
			var uvScale = EditorGUILayout.FloatField("Scale", polyMesh.uvScale);
			var uvRotation = EditorGUILayout.Slider("Rotation", polyMesh.uvRotation, -180, 180) % 360;
			if (uvRotation < -180)
				uvRotation += 360;
			if (GUI.changed)
			{
				RecordUndo();
				polyMesh.uvPosition = uvPosition;
				polyMesh.uvScale = uvScale;
				polyMesh.uvRotation = uvRotation;
			}
			if (GUILayout.Button("Reset UVs"))
			{
				polyMesh.uvPosition = Vector3.zero;
				polyMesh.uvScale = 1;
				polyMesh.uvRotation = 0;
			}
		}

		//Mesh settings
		if (meshSettings = EditorGUILayout.Foldout(meshSettings, "Mesh"))
		{
			var curveDetail = EditorGUILayout.Slider("Curve Detail", polyMesh.curveDetail, 0.01f, 1f);
			curveDetail = Mathf.Clamp(curveDetail, 0.01f, 1f);
			if (GUI.changed)
			{
				RecordUndo();
				polyMesh.curveDetail = curveDetail;
			}

			//Buttons
			EditorGUILayout.BeginHorizontal();
			if (GUILayout.Button("Build Mesh"))
				polyMesh.BuildMesh();
			if (GUILayout.Button("Make Mesh Unique"))
			{
				RecordUndo();
				polyMesh.GetComponent<MeshFilter>().mesh = null;
				polyMesh.BuildMesh();
			}
			EditorGUILayout.EndHorizontal();
		}

		//Create collider
		if (colliderSettings = EditorGUILayout.Foldout(colliderSettings, "Collider"))
		{
			//Collider depth
			var colliderDepth = EditorGUILayout.FloatField("Depth", polyMesh.colliderDepth);
			colliderDepth = Mathf.Max(colliderDepth, 0.01f);
			var buildColliderEdges = EditorGUILayout.Toggle("Build Edges", polyMesh.buildColliderEdges);
			var buildColliderFront = EditorGUILayout.Toggle("Build Font", polyMesh.buildColliderFront);
			if (GUI.changed)
			{
				RecordUndo();
				polyMesh.colliderDepth = colliderDepth;
				polyMesh.buildColliderEdges = buildColliderEdges;
				polyMesh.buildColliderFront = buildColliderFront;
			}

			//Destroy collider
			if (polyMesh.meshCollider == null)
			{
				if (GUILayout.Button("Create Collider"))
				{
					RecordDeepUndo();
					var obj = new GameObject("Collider", typeof(MeshCollider));
					polyMesh.meshCollider = obj.GetComponent<MeshCollider>();
					obj.transform.parent = polyMesh.transform;
					obj.transform.localPosition = Vector3.zero;
				}
			}
			else if (GUILayout.Button("Destroy Collider"))
			{
				RecordDeepUndo();
				DestroyImmediate(polyMesh.meshCollider.gameObject);
			}
		}

		//Update mesh
		if (GUI.changed)
			polyMesh.BuildMesh();

		//Editor settings
		if (editorSettings = EditorGUILayout.Foldout(editorSettings, "Editor"))
		{
			gridSnap = EditorGUILayout.FloatField("Grid Snap", gridSnap);
			autoSnap = EditorGUILayout.Toggle("Auto Snap", autoSnap);
			globalSnap = EditorGUILayout.Toggle("Global Snap", globalSnap);
			EditorGUI.BeginChangeCheck();
			hideWireframe = EditorGUILayout.Toggle("Hide Wireframe", hideWireframe);
			if (EditorGUI.EndChangeCheck())
				HideWireframe(hideWireframe);

			editKey = (KeyCode)EditorGUILayout.EnumPopup("[Toggle Edit] Key", editKey);
			selectAllKey = (KeyCode)EditorGUILayout.EnumPopup("[Select All] Key", selectAllKey);
			splitKey = (KeyCode)EditorGUILayout.EnumPopup("[Split] Key", splitKey);
			extrudeKey = (KeyCode)EditorGUILayout.EnumPopup("[Extrude] Key", extrudeKey);
		}
	}

	#endregion

	#region Scene GUI

	void OnSceneGUI()
	{
		if (target == null)
			return;

		if (KeyPressed(editKey))
			editing = !editing;

		if (editing)
		{
			//Update lists
			if (keyPoints == null)
			{
				keyPoints = new List<Vector3>(polyMesh.keyPoints);
				curvePoints = new List<Vector3>(polyMesh.curvePoints);
				isCurve = new List<bool>(polyMesh.isCurve);
			}

			//Crazy hack to register undo
			if (undoCallback == null)
			{
				undoCallback = typeof(EditorApplication).GetField("undoRedoPerformed", BindingFlags.NonPublic | BindingFlags.Static);
				if (undoCallback != null)
					undoCallback.SetValue(null, new EditorApplication.CallbackFunction(OnUndoRedo));
			}

			//Load handle matrix
			Handles.matrix = polyMesh.transform.localToWorldMatrix;

			//Draw points and lines
			DrawAxis();
			Handles.color = Color.white;
			for (int i = 0; i < keyPoints.Count; i++)
			{
				Handles.color = nearestLine == i ? Color.green : Color.white;
				DrawSegment(i);
				if (selectedIndices.Contains(i))
				{
					Handles.color = Color.green;
					DrawCircle(keyPoints[i], 0.08f);
				}
				else
					Handles.color = Color.white;
				DrawKeyPoint(i);
				if (isCurve[i])
				{
					Handles.color = (draggingCurve && dragIndex == i) ? Color.white : Color.blue;
					DrawCurvePoint(i);
				}
			}

			//Quit on tool change
			if (e.type == EventType.KeyDown)
			{
				switch (e.keyCode)
				{
				case KeyCode.Q:
				case KeyCode.W:
				case KeyCode.E:
				case KeyCode.R:
					return;
				}
			}

			//Quit if panning or no camera exists
			if (Tools.current == Tool.View || (e.isMouse && e.button > 0) || Camera.current == null || e.type == EventType.ScrollWheel)
				return;

			//Quit if laying out
			if (e.type == EventType.Layout)
			{
				HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
				return;
			}

			//Cursor rectangle
			EditorGUIUtility.AddCursorRect(new Rect(0, 0, Camera.current.pixelWidth, Camera.current.pixelHeight), mouseCursor);
			mouseCursor = MouseCursor.Arrow;

			//Extrude key state
			if (e.keyCode == extrudeKey)
			{
				if (extrudeKeyDown)
				{
					if (e.type == EventType.KeyUp)
						extrudeKeyDown = false;
				}
				else if (e.type == EventType.KeyDown)
					extrudeKeyDown = true;
			}

			//Update matrices and snap
			worldToLocal = polyMesh.transform.worldToLocalMatrix;
			inverseRotation = Quaternion.Inverse(polyMesh.transform.rotation) * Camera.current.transform.rotation;
			snap = gridSnap;
			
			//Update mouse position
			screenMousePosition = new Vector3(e.mousePosition.x, Camera.current.pixelHeight - e.mousePosition.y);
			var plane = new Plane(-polyMesh.transform.forward, polyMesh.transform.position);
			var ray = Camera.current.ScreenPointToRay(screenMousePosition);
			float hit;
			if (plane.Raycast(ray, out hit))
				mousePosition = worldToLocal.MultiplyPoint(ray.GetPoint(hit));
			else
				return;

			//Update nearest line and split position
			nearestLine = NearestLine(out splitPosition);
			
			//Update the state and repaint
			var newState = UpdateState();
			if (state != newState)
				SetState(newState);
			HandleUtility.Repaint();
			e.Use();
		}
	}

	void HideWireframe(bool hide)
	{
		if (polyMesh.renderer != null)
			EditorUtility.SetSelectedWireframeHidden(polyMesh.renderer, hide);
	}

	void RecordUndo()
	{
#if UNITY_4_3
		Undo.RecordObject(target, "PolyMesh Changed");
#else
		Undo.RegisterUndo(target, "PolyMesh Changed");
#endif
	}

	void RecordDeepUndo()
	{
#if UNITY_4_3
		Undo.RegisterFullObjectHierarchyUndo(target);
#else
		Undo.RegisterSceneUndo("PolyMesh Changed");
#endif

	}

	#endregion

	#region State Control

	//Initialize state
	void SetState(State newState)
	{
		state = newState;
		switch (state)
		{
		case State.Hover:
			break;
		}
	}

	//Update state
	State UpdateState()
	{
		switch (state)
		{
			//Hovering
		case State.Hover:

			DrawNearestLineAndSplit();

			if (Tools.current == Tool.Move && TryDragSelected())
				return State.DragSelected;
			if (Tools.current == Tool.Rotate && TryRotateSelected())
				return State.RotateSelected;
			if (Tools.current == Tool.Scale && TryScaleSelected())
				return State.ScaleSelected;
			if (Tools.current == Tool.Move && TryExtrude())
				return State.Extrude;

			if (TrySelectAll())
				return State.Hover;
			if (TrySplitLine())
				return State.Hover;
			if (TryDeleteSelected())
				return State.Hover;

			if (TryHoverCurvePoint(out dragIndex) && TryDragCurvePoint(dragIndex))
				return State.Drag;
			if (TryHoverKeyPoint(out dragIndex) && TryDragKeyPoint(dragIndex))
				return State.Drag;
			if (TryBoxSelect())
				return State.BoxSelect;

			break;

			//Dragging
		case State.Drag:
			mouseCursor = MouseCursor.MoveArrow;
			DrawCircle(keyPoints[dragIndex], clickRadius);
			if (draggingCurve)
				MoveCurvePoint(dragIndex, mousePosition - clickPosition);
			else
				MoveKeyPoint(dragIndex, mousePosition - clickPosition);
			if (TryStopDrag())
				return State.Hover;
			break;

			//Box Selecting
		case State.BoxSelect:
			if (TryBoxSelectEnd())
				return State.Hover;
			break;

			//Dragging selected
		case State.DragSelected:
			mouseCursor = MouseCursor.MoveArrow;
			MoveSelected(mousePosition - clickPosition);
			if (TryStopDrag())
				return State.Hover;
			break;

			//Rotating selected
		case State.RotateSelected:
			mouseCursor = MouseCursor.RotateArrow;
			RotateSelected();
			if (TryStopDrag())
				return State.Hover;
			break;

			//Scaling selected
		case State.ScaleSelected:
			mouseCursor = MouseCursor.ScaleArrow;
			ScaleSelected();
			if (TryStopDrag())
				return State.Hover;
			break;

			//Extruding
		case State.Extrude:
			mouseCursor = MouseCursor.MoveArrow;
			MoveSelected(mousePosition - clickPosition);
			if (doExtrudeUpdate && mousePosition != clickPosition)
			{
				UpdatePoly(false, false);
				doExtrudeUpdate = false;
			}
			if (TryStopDrag())
				return State.Hover;
			break;
		}
		return state;
	}

	//Update the mesh on undo/redo
	void OnUndoRedo()
	{
		keyPoints = new List<Vector3>(polyMesh.keyPoints);
		curvePoints = new List<Vector3>(polyMesh.curvePoints);
		isCurve = new List<bool>(polyMesh.isCurve);
		polyMesh.BuildMesh();
	}
	
	void LoadPoly()
	{
		for (int i = 0; i < keyPoints.Count; i++)
		{
			keyPoints[i] = polyMesh.keyPoints[i];
			curvePoints[i] = polyMesh.curvePoints[i];
			isCurve[i] = polyMesh.isCurve[i];
		}
	}
	
	void TransformPoly(Matrix4x4 matrix)
	{
		for (int i = 0; i < keyPoints.Count; i++)
		{
			keyPoints[i] = matrix.MultiplyPoint(polyMesh.keyPoints[i]);
			curvePoints[i] = matrix.MultiplyPoint(polyMesh.curvePoints[i]);
		}
	}
	
	void UpdatePoly(bool sizeChanged, bool recordUndo)
	{
		if (recordUndo)
			RecordUndo();
		if (sizeChanged)
		{
			polyMesh.keyPoints = new List<Vector3>(keyPoints);
			polyMesh.curvePoints = new List<Vector3>(curvePoints);
			polyMesh.isCurve = new List<bool>(isCurve);
		}
		else
		{
			for (int i = 0; i < keyPoints.Count; i++)
			{
				polyMesh.keyPoints[i] = keyPoints[i];
				polyMesh.curvePoints[i] = curvePoints[i];
				polyMesh.isCurve[i] = isCurve[i];
			}
		}
		for (int i = 0; i < keyPoints.Count; i++)
			if (!isCurve[i])
				polyMesh.curvePoints[i] = curvePoints[i] = Vector3.Lerp(keyPoints[i], keyPoints[(i + 1) % keyPoints.Count], 0.5f);
		polyMesh.BuildMesh();
	}

	void MoveKeyPoint(int index, Vector3 amount)
	{
		var moveCurve = selectedIndices.Contains((index + 1) % keyPoints.Count);
		if (doSnap)
		{
			if (globalSnap)
			{
				keyPoints[index] = Snap(polyMesh.keyPoints[index] + amount);
				if (moveCurve)
					curvePoints[index] = Snap(polyMesh.curvePoints[index] + amount);
			}
			else
			{
				amount = Snap(amount);
				keyPoints[index] = polyMesh.keyPoints[index] + amount;
				if (moveCurve)
					curvePoints[index] = polyMesh.curvePoints[index] + amount;
			}
		}
		else
		{
			keyPoints[index] = polyMesh.keyPoints[index] + amount;
			if (moveCurve)
				curvePoints[index] = polyMesh.curvePoints[index] + amount;
		}
	}

	void MoveCurvePoint(int index, Vector3 amount)
	{
		isCurve[index] = true;
		if (doSnap)
		{
			if (globalSnap)
				curvePoints[index] = Snap(polyMesh.curvePoints[index] + amount);
			else
				curvePoints[index] = polyMesh.curvePoints[index] + amount;
		}
		else
			curvePoints[index] = polyMesh.curvePoints[index] + amount;
	}


	void MoveSelected(Vector3 amount)
	{
		foreach (var i in selectedIndices)
			MoveKeyPoint(i, amount);
	}

	void RotateSelected()
	{
		var center = GetSelectionCenter();

		Handles.color = Color.white;
		Handles.DrawLine(center, clickPosition);
		Handles.color = Color.green;
		Handles.DrawLine(center, mousePosition);

		var clickOffset = clickPosition - center;
		var mouseOffset = mousePosition - center;
		var clickAngle = Mathf.Atan2(clickOffset.y, clickOffset.x);
		var mouseAngle = Mathf.Atan2(mouseOffset.y, mouseOffset.x);
		var angleOffset = mouseAngle - clickAngle;

		foreach (var i in selectedIndices)
		{
			var point = polyMesh.keyPoints[i];
			var pointOffset = point - center;
			var a = Mathf.Atan2(pointOffset.y, pointOffset.x) + angleOffset;
			var d = pointOffset.magnitude;
			keyPoints[i] = center + new Vector3(Mathf.Cos(a) * d, Mathf.Sin(a) * d);
		}
	}

	void ScaleSelected()
	{
		Handles.color = Color.green;
		Handles.DrawLine(clickPosition, mousePosition);

		var center = GetSelectionCenter();
		var scale = mousePosition - clickPosition;

		//Uniform scaling if shift pressed
		if (e.shift)
		{
			if (Mathf.Abs(scale.x) > Mathf.Abs(scale.y))
				scale.y = scale.x;
			else
				scale.x = scale.y;
		}

		//Determine direction of scaling
		if (scale.x < 0)
			scale.x = 1 / (-scale.x + 1);
		else
			scale.x = 1 + scale.x;
		if (scale.y < 0)
			scale.y = 1 / (-scale.y + 1);
		else
			scale.y = 1 + scale.y;

		foreach (var i in selectedIndices)
		{
			var point = polyMesh.keyPoints[i];
			var offset = point - center;
			offset.x *= scale.x;
			offset.y *= scale.y;
			keyPoints[i] = center + offset;
		}
	}

	#endregion

	#region Drawing

	void DrawAxis()
	{
		Handles.color = Color.red;
		var size = HandleUtility.GetHandleSize(Vector3.zero) * 0.1f;
		Handles.DrawLine(new Vector3(-size, 0), new Vector3(size, 0));
		Handles.DrawLine(new Vector3(0, -size), new Vector2(0, size));
	}

	void DrawKeyPoint(int index)
	{
		Handles.DotCap(0, keyPoints[index], Quaternion.identity, HandleUtility.GetHandleSize(keyPoints[index]) * 0.03f);
	}

	void DrawCurvePoint(int index)
	{
		Handles.DotCap(0, curvePoints[index], Quaternion.identity, HandleUtility.GetHandleSize(keyPoints[index]) * 0.03f);
	}

	void DrawSegment(int index)
	{
		var from = keyPoints[index];
		var to = keyPoints[(index + 1) % keyPoints.Count];
		if (isCurve[index])
		{
			var control = Bezier.Control(from, to, curvePoints[index]);
			var count = Mathf.Ceil(1 / polyMesh.curveDetail);
			for (int i = 0; i < count; i++)
				Handles.DrawLine(Bezier.Curve(from, control, to, i / count), Bezier.Curve(from, control, to, (i + 1) / count));
		}
		else
			Handles.DrawLine(from, to);
	}

	void DrawCircle(Vector3 position, float size)
	{
		Handles.CircleCap(0, position, inverseRotation, HandleUtility.GetHandleSize(position) * size);
	}

	void DrawNearestLineAndSplit()
	{
		if (nearestLine >= 0)
		{
			Handles.color = Color.green;
			DrawSegment(nearestLine);
			Handles.color = Color.red;
			Handles.DotCap(0, splitPosition, Quaternion.identity, HandleUtility.GetHandleSize(splitPosition) * 0.03f);
		}
	}

	#endregion

	#region State Checking

	bool TryHoverKeyPoint(out int index)
	{
		if (TryHover(keyPoints, Color.white, out index))
		{
			mouseCursor = MouseCursor.MoveArrow;
			return true;
		}
		return false;
	}

	bool TryHoverCurvePoint(out int index)
	{
		if (TryHover(curvePoints, Color.white, out index))
		{
			mouseCursor = MouseCursor.MoveArrow;
			return true;
		}
		return false;
	}

	bool TryDragKeyPoint(int index)
	{
		if (TryDrag(keyPoints, index))
		{
			draggingCurve = false;
			return true;
		}
		return false;
	}

	bool TryDragCurvePoint(int index)
	{
		if (TryDrag(curvePoints, index))
		{
			draggingCurve = true;
			return true;
		}
		return false;
	}

	bool TryHover(List<Vector3> points, Color color, out int index)
	{
		if (Tools.current == Tool.Move)
		{
			index = NearestPoint(points);
			if (index >= 0 && IsHovering(points[index]))
			{
				Handles.color = color;
				DrawCircle(points[index], clickRadius);
				return true;
			}
		}
		index = -1;
		return false;
	}

	bool TryDrag(List<Vector3> points, int index)
	{
		if (e.type == EventType.MouseDown && IsHovering(points[index]))
		{
			clickPosition = mousePosition;
			return true;
		}
		return false;
	}

	bool TryStopDrag()
	{
		if (e.type == EventType.MouseUp)
		{
			dragIndex = -1;
			UpdatePoly(false, state != State.Extrude);
			return true;
		}
		return false;
	}

	bool TryBoxSelect()
	{
		if (e.type == EventType.MouseDown)
		{
			clickPosition = mousePosition;
			return true;
		}
		return false;
	}

	bool TryBoxSelectEnd()
	{
		var min = new Vector3(Mathf.Min(clickPosition.x, mousePosition.x), Mathf.Min(clickPosition.y, mousePosition.y));
		var max = new Vector3(Mathf.Max(clickPosition.x, mousePosition.x), Mathf.Max(clickPosition.y, mousePosition.y));
		Handles.color = Color.white;
		Handles.DrawLine(new Vector3(min.x, min.y), new Vector3(max.x, min.y));
		Handles.DrawLine(new Vector3(min.x, max.y), new Vector3(max.x, max.y));
		Handles.DrawLine(new Vector3(min.x, min.y), new Vector3(min.x, max.y));
		Handles.DrawLine(new Vector3(max.x, min.y), new Vector3(max.x, max.y));

		if (e.type == EventType.MouseUp)
		{
			var rect = new Rect(min.x, min.y, max.x - min.x, max.y - min.y);

			if (!control)
				selectedIndices.Clear();
			for (int i = 0; i < keyPoints.Count; i++)
				if (rect.Contains(keyPoints[i]))
					selectedIndices.Add(i);

			return true;
		}
		return false;
	}

	bool TryDragSelected()
	{
		if (selectedIndices.Count > 0 && TryDragButton(GetSelectionCenter(), 0.2f))
		{
			clickPosition = mousePosition;
			return true;
		}
		return false;
	}

	bool TryRotateSelected()
	{
		if (selectedIndices.Count > 0 && TryRotateButton(GetSelectionCenter(), 0.3f))
		{
			clickPosition = mousePosition;
			return true;
		}
		return false;
	}

	bool TryScaleSelected()
	{
		if (selectedIndices.Count > 0 && TryScaleButton(GetSelectionCenter(), 0.3f))
		{
			clickPosition = mousePosition;
			return true;
		}
		return false;
	}

	bool TryDragButton(Vector3 position, float size)
	{
		size *= HandleUtility.GetHandleSize(position);
		if (Vector3.Distance(mousePosition, position) < size)
		{
			if (e.type == EventType.MouseDown)
				return true;
			else
			{
				mouseCursor = MouseCursor.MoveArrow;
				Handles.color = Color.green;
			}
		}
		else
			Handles.color = Color.white;
		var buffer = size / 2;
		Handles.DrawLine(new Vector3(position.x - buffer, position.y), new Vector3(position.x + buffer, position.y));
		Handles.DrawLine(new Vector3(position.x, position.y - buffer), new Vector3(position.x, position.y + buffer));
		Handles.RectangleCap(0, position, Quaternion.identity, size);
		return false;
	}

	bool TryRotateButton(Vector3 position, float size)
	{
		size *= HandleUtility.GetHandleSize(position);
		var dist = Vector3.Distance(mousePosition, position);
		var buffer = size / 4;
		if (dist < size + buffer && dist > size - buffer)
		{
			if (e.type == EventType.MouseDown)
				return true;
			else
			{
				mouseCursor = MouseCursor.RotateArrow;
				Handles.color = Color.green;
			}
		}
		else
			Handles.color = Color.white;
		Handles.CircleCap(0, position, inverseRotation, size - buffer / 2);
		Handles.CircleCap(0, position, inverseRotation, size + buffer / 2);
		return false;
	}

	bool TryScaleButton(Vector3 position, float size)
	{
		size *= HandleUtility.GetHandleSize(position);
		if (Vector3.Distance(mousePosition, position) < size)
		{
			if (e.type == EventType.MouseDown)
				return true;
			else
			{
				mouseCursor = MouseCursor.ScaleArrow;
				Handles.color = Color.green;
			}
		}
		else
			Handles.color = Color.white;
		var buffer = size / 4;
		Handles.DrawLine(new Vector3(position.x - size - buffer, position.y), new Vector3(position.x - size + buffer, position.y));
		Handles.DrawLine(new Vector3(position.x + size - buffer, position.y), new Vector3(position.x + size + buffer, position.y));
		Handles.DrawLine(new Vector3(position.x, position.y - size - buffer), new Vector3(position.x, position.y - size + buffer));
		Handles.DrawLine(new Vector3(position.x, position.y + size - buffer), new Vector3(position.x, position.y + size + buffer));
		Handles.RectangleCap(0, position, Quaternion.identity, size);
		return false;
	}

	bool TrySelectAll()
	{
		if (KeyPressed(selectAllKey))
		{
			selectedIndices.Clear();
			for (int i = 0; i < keyPoints.Count; i++)
				selectedIndices.Add(i);
			return true;
		}
		return false;
	}

	bool TrySplitLine()
	{
		if (nearestLine >= 0 && KeyPressed(splitKey))
		{
			if (nearestLine == keyPoints.Count - 1)
			{
				keyPoints.Add(splitPosition);
				curvePoints.Add(Vector3.zero);
				isCurve.Add(false);
			}
			else
			{
				keyPoints.Insert(nearestLine + 1, splitPosition);
				curvePoints.Insert(nearestLine + 1, Vector3.zero);
				isCurve.Insert(nearestLine + 1, false);
			}
			isCurve[nearestLine] = false;
			UpdatePoly(true, true);
			return true;
		}
		return false;
	}

	bool TryExtrude()
	{
		if (nearestLine >= 0 && extrudeKeyDown && e.type == EventType.MouseDown)
		{
			var a = nearestLine;
			var b = (nearestLine + 1) % keyPoints.Count;
			if (b == 0 && a == keyPoints.Count - 1)
			{
				//Extrude between the first and last points
				keyPoints.Add(polyMesh.keyPoints[a]);
				keyPoints.Add(polyMesh.keyPoints[b]);
				curvePoints.Add(Vector3.zero);
				curvePoints.Add(Vector3.zero);
				isCurve.Add(false);
				isCurve.Add(false);
				
				selectedIndices.Clear();
				selectedIndices.Add(keyPoints.Count - 2);
				selectedIndices.Add(keyPoints.Count - 1);
			}
			else
			{
				//Extrude between two inner points
				var pointA = keyPoints[a];
				var pointB = keyPoints[b];
				keyPoints.Insert(a + 1, pointA);
				keyPoints.Insert(a + 2, pointB);
				curvePoints.Insert(a + 1, Vector3.zero);
				curvePoints.Insert(a + 2, Vector3.zero);
				isCurve.Insert(a + 1, false);
				isCurve.Insert(a + 2, false);
				
				selectedIndices.Clear();
				selectedIndices.Add(a + 1);
				selectedIndices.Add(a + 2);
			}
			isCurve[nearestLine] = false;

			clickPosition = mousePosition;
			doExtrudeUpdate = true;
			UpdatePoly(true, true);
			return true;
		}
		return false;
	}

	bool TryDeleteSelected()
	{
		if (KeyPressed(KeyCode.Backspace) || KeyPressed(KeyCode.Delete))
		{
			if (selectedIndices.Count > 0)
			{
				if (keyPoints.Count - selectedIndices.Count >= 3)
				{
					for (int i = selectedIndices.Count - 1; i >= 0; i--)
					{
						var index = selectedIndices[i];
						keyPoints.RemoveAt(index);
						curvePoints.RemoveAt(index);
						isCurve.RemoveAt(index);
					}
					selectedIndices.Clear();
					UpdatePoly(true, true);
					return true;
				}
			}
			else if (IsHovering(curvePoints[nearestLine]))
			{
				isCurve[nearestLine] = false;
				UpdatePoly(false, true);
			}
		}
		return false;
	}

	bool IsHovering(Vector3 point)
	{
		return Vector3.Distance(mousePosition, point) < HandleUtility.GetHandleSize(point) * clickRadius;
	}
	
	int NearestPoint(List<Vector3> points)
	{
		var near = -1;
		var nearDist = float.MaxValue;
		for (int i = 0; i < points.Count; i++)
		{
			var dist = Vector3.Distance(points[i], mousePosition);
			if (dist < nearDist)
			{
				nearDist = dist;
				near = i;
			}
		}
		return near;
	}
	
	int NearestLine(out Vector3 position)
	{
		var near = -1;
		var nearDist = float.MaxValue;
		position = keyPoints[0];
		var linePos = Vector3.zero;
		for (int i = 0; i < keyPoints.Count; i++)
		{
			var j = (i + 1) % keyPoints.Count;
			var line = keyPoints[j] - keyPoints[i];
			var offset = mousePosition - keyPoints[i];
			var dot = Vector3.Dot(line.normalized, offset);
			if (dot >= 0 && dot <= line.magnitude)
			{
				if (isCurve[i])
					linePos = Bezier.Curve(keyPoints[i], Bezier.Control(keyPoints[i], keyPoints[j], curvePoints[i]), keyPoints[j], dot / line.magnitude);
				else
					linePos = keyPoints[i] + line.normalized * dot;
				var dist = Vector3.Distance(linePos, mousePosition);
				if (dist < nearDist)
				{
					nearDist = dist;
					position = linePos;
					near = i;
				}
			}
		}
		return near;
	}

	bool KeyPressed(KeyCode key)
	{
		return e.type == EventType.KeyDown && e.keyCode == key;
	}

	bool KeyReleased(KeyCode key)
	{
		return e.type == EventType.KeyUp && e.keyCode == key;
	}
	
	Vector3 Snap(Vector3 value)
	{
		value.x = Mathf.Round(value.x / snap) * snap;
		value.y = Mathf.Round(value.y / snap) * snap;
		return value;
	}

	Vector3 GetSelectionCenter()
	{
		var center = Vector3.zero;
		foreach (var i in selectedIndices)
			center += polyMesh.keyPoints[i];
		return center / selectedIndices.Count;
	}

	#endregion

	#region Properties

	PolyMesh polyMesh
	{
		get { return (PolyMesh)target; }
	}

	Event e
	{
		get { return Event.current; }
	}

	bool control
	{
		get { return Application.platform == RuntimePlatform.OSXEditor ? e.command : e.control; }
	}

	bool doSnap
	{
		get { return autoSnap ? !control : control; }
	}

	static bool meshSettings
	{
		get { return EditorPrefs.GetBool("PolyMeshEditor_meshSettings", false); }
		set { EditorPrefs.SetBool("PolyMeshEditor_meshSettings", value); }
	}

	static bool colliderSettings
	{
		get { return EditorPrefs.GetBool("PolyMeshEditor_colliderSettings", false); }
		set { EditorPrefs.SetBool("PolyMeshEditor_colliderSettings", value); }
	}

	static bool uvSettings
	{
		get { return EditorPrefs.GetBool("PolyMeshEditor_uvSettings", false); }
		set { EditorPrefs.SetBool("PolyMeshEditor_uvSettings", value); }
	}

	static bool editorSettings
	{
		get { return EditorPrefs.GetBool("PolyMeshEditor_editorSettings", false); }
		set { EditorPrefs.SetBool("PolyMeshEditor_editorSettings", value); }
	}

	static bool autoSnap
	{
		get { return EditorPrefs.GetBool("PolyMeshEditor_autoSnap", false); }
		set { EditorPrefs.SetBool("PolyMeshEditor_autoSnap", value); }
	}

	static bool globalSnap
	{
		get { return EditorPrefs.GetBool("PolyMeshEditor_globalSnap", false); }
		set { EditorPrefs.SetBool("PolyMeshEditor_globalSnap", value); }
	}

	static float gridSnap
	{
		get { return EditorPrefs.GetFloat("PolyMeshEditor_gridSnap", 1); }
		set { EditorPrefs.SetFloat("PolyMeshEditor_gridSnap", value); }
	}

	static bool hideWireframe
	{
		get { return EditorPrefs.GetBool("PolyMeshEditor_hideWireframe", true); }
		set { EditorPrefs.SetBool("PolyMeshEditor_hideWireframe", value); }
	}

	public KeyCode editKey
	{
		get { return (KeyCode)EditorPrefs.GetInt("PolyMeshEditor_editKey", (int)KeyCode.Tab); }
		set { EditorPrefs.SetInt("PolyMeshEditor_editKey", (int)value); }
	}

	public KeyCode selectAllKey
	{
		get { return (KeyCode)EditorPrefs.GetInt("PolyMeshEditor_selectAllKey", (int)KeyCode.A); }
		set { EditorPrefs.SetInt("PolyMeshEditor_selectAllKey", (int)value); }
	}

	public KeyCode splitKey
	{
		get { return (KeyCode)EditorPrefs.GetInt("PolyMeshEditor_splitKey", (int)KeyCode.S); }
		set { EditorPrefs.SetInt("PolyMeshEditor_splitKey", (int)value); }
	}

	public KeyCode extrudeKey
	{
		get { return (KeyCode)EditorPrefs.GetInt("PolyMeshEditor_extrudeKey", (int)KeyCode.D); }
		set { EditorPrefs.SetInt("PolyMeshEditor_extrudeKey", (int)value); }
	}

	#endregion

	#region Menu Items

	[MenuItem("GameObject/Create Other/PolyMesh", false, 1000)]
	static void CreatePolyMesh()
	{
		var obj = new GameObject("PolyMesh", typeof(MeshFilter), typeof(MeshRenderer));
		var polyMesh = obj.AddComponent<PolyMesh>();
		CreateSquare(polyMesh, 0.5f);
	}

	static void CreateSquare(PolyMesh polyMesh, float size)
	{
		polyMesh.keyPoints.AddRange(new Vector3[] { new Vector3(size, size), new Vector3(size, -size), new Vector3(-size, -size), new Vector3(-size, size)} );
		polyMesh.curvePoints.AddRange(new Vector3[] { Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero } );
		polyMesh.isCurve.AddRange(new bool[] { false, false, false, false } );
		polyMesh.BuildMesh();
	}

	#endregion
}
