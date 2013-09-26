using UnityEngine;
using System.Collections;

public class RollingRock : MonoBehaviour
{
	public float rollTorque;

	void FixedUpdate()
	{
		rigidbody.AddTorque(0, 0, rollTorque, ForceMode.Acceleration);
	}
}
