/* Copyright (c) Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

/*
 * Leak check for joltc's contact listener and for a physics system's create/destroy, run under
 * AddressSanitizer's leak checker by .github/workflows/joltc-leakcheck.yml. Everything this program creates is
 * destroyed before it exits, so whatever the leak checker reports at exit was lost inside joltc.
 *
 *   leakcheck validate  <steps>   contact listener with all four callbacks set, as JoltPhysicsSharp sets them
 *   leakcheck novalidate <steps>  the same with the OnContactValidate slot empty
 *   leakcheck nolistener <steps>  no contact listener
 *   leakcheck restart <cycles>    whole systems (terrain + 100 boxes, 20 steps each) created and destroyed
 *
 * The scene: a pile of 100 dynamic boxes on a static floor (a height field in restart mode), a share of them
 * kicked upwards every step so they stay awake and keep colliding.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "joltc.h"

static long s_validates;

static JPH_ValidateResult OnValidate(void* userData, const JPH_Body* body1, const JPH_Body* body2,
	const JPH_RVec3* baseOffset, const JPH_CollideShapeResult* result)
{
	(void)userData; (void)body1; (void)body2; (void)baseOffset; (void)result;
	s_validates++;
	return JPH_ValidateResult_AcceptAllContactsForThisBodyPair;
}

static void OnAdded(void* userData, const JPH_Body* body1, const JPH_Body* body2,
	const JPH_ContactManifold* manifold, JPH_ContactSettings* settings)
{
	(void)userData; (void)body1; (void)body2; (void)manifold; (void)settings;
}

static void OnRemoved(void* userData, const JPH_SubShapeIDPair* pair)
{
	(void)userData; (void)pair;
}

static JPH_ContactListener_Procs s_procs;

typedef struct Scene {
	JPH_BroadPhaseLayerInterface* bpi;
	JPH_ObjectLayerPairFilter* olp;
	JPH_ObjectVsBroadPhaseLayerFilter* ovb;
	JPH_PhysicsSystem* system;
	JPH_ContactListener* listener;
	JPH_Shape* floor;
	JPH_Shape* box;
	JPH_BodyID floorId;
	JPH_BodyID ids[100];
} Scene;

static void SceneCreate(Scene* s, int listener, int heightField)
{
	memset(s, 0, sizeof(*s));
	s->olp = JPH_ObjectLayerPairFilterTable_Create(2);
	JPH_ObjectLayerPairFilterTable_EnableCollision(s->olp, 0, 1);
	JPH_ObjectLayerPairFilterTable_EnableCollision(s->olp, 1, 1);
	s->bpi = JPH_BroadPhaseLayerInterfaceTable_Create(2, 2);
	JPH_BroadPhaseLayerInterfaceTable_MapObjectToBroadPhaseLayer(s->bpi, 0, 0);
	JPH_BroadPhaseLayerInterfaceTable_MapObjectToBroadPhaseLayer(s->bpi, 1, 1);
	s->ovb = JPH_ObjectVsBroadPhaseLayerFilterTable_Create(s->bpi, 2, s->olp, 2);

	JPH_PhysicsSystemSettings settings;
	memset(&settings, 0, sizeof(settings));
	settings.maxBodies = 65536;
	settings.maxBodyPairs = 65536;
	settings.maxContactConstraints = 10240;
	settings.broadPhaseLayerInterface = s->bpi;
	settings.objectLayerPairFilter = s->olp;
	settings.objectVsBroadPhaseLayerFilter = s->ovb;
	s->system = JPH_PhysicsSystem_Create(&settings);
	JPH_Vec3 gravity = { 0.0f, -9.8f, 0.0f };
	JPH_PhysicsSystem_SetGravity(s->system, &gravity);
	if (listener)
	{
		s->listener = JPH_ContactListener_Create(NULL);
		JPH_PhysicsSystem_SetContactListener(s->system, s->listener);
	}

	JPH_BodyInterface* bi = JPH_PhysicsSystem_GetBodyInterface(s->system);
	JPH_RVec3 floorPos = { 0.0f, -1.0f, 0.0f };
	if (heightField)
	{
		enum { N = 257 };
		float* samples = (float*)malloc(sizeof(float) * N * N);
		for (int i = 0; i < N * N; i++)
			samples[i] = 1.0f;
		JPH_Vec3 offset = { -128.0f, 0.0f, -128.0f };
		JPH_Vec3 scale = { 1.0f, 1.0f, 1.0f };
		JPH_HeightFieldShapeSettings* hs = JPH_HeightFieldShapeSettings_Create(samples, &offset, &scale, N, NULL);
		s->floor = (JPH_Shape*)JPH_HeightFieldShapeSettings_CreateShape(hs);
		JPH_ShapeSettings_Destroy((JPH_ShapeSettings*)hs);
		free(samples);
		floorPos.y = -1.0f;
	}
	else
	{
		JPH_Vec3 half = { 100.0f, 1.0f, 100.0f };
		s->floor = (JPH_Shape*)JPH_BoxShape_Create(&half, 0.05f);
	}
	JPH_Quat identity = { 0.0f, 0.0f, 0.0f, 1.0f };
	JPH_BodyCreationSettings* fs = JPH_BodyCreationSettings_Create3(s->floor, &floorPos, &identity, JPH_MotionType_Static, 0);
	s->floorId = JPH_BodyInterface_CreateAndAddBody(bi, fs, JPH_Activation_DontActivate);
	JPH_BodyCreationSettings_Destroy(fs);

	JPH_Vec3 boxHalf = { 0.4f, 0.4f, 0.4f };
	s->box = (JPH_Shape*)JPH_BoxShape_Create(&boxHalf, 0.05f);
	for (int i = 0; i < 100; i++)
	{
		int x = i % 6, z = (i / 6) % 6, y = i / 36;
		JPH_RVec3 p = { x * 0.85f + (y % 2) * 0.3f, 0.5f + y * 0.9f, z * 0.85f + (y % 2) * 0.3f };
		JPH_BodyCreationSettings* bs = JPH_BodyCreationSettings_Create3(s->box, &p, &identity, JPH_MotionType_Dynamic, 1);
		s->ids[i] = JPH_BodyInterface_CreateAndAddBody(bi, bs, JPH_Activation_Activate);
		JPH_BodyCreationSettings_Destroy(bs);
	}
}

static void SceneStep(Scene* s, JPH_JobSystem* jobs, int steps)
{
	JPH_BodyInterface* bi = JPH_PhysicsSystem_GetBodyInterface(s->system);
	JPH_Vec3 kick = { 0.0f, 3.0f, 0.0f };
	for (int k = 0; k < steps; k++)
	{
		for (int i = k % 11; i < 100; i += 11)
			JPH_BodyInterface_SetLinearVelocity(bi, s->ids[i], &kick);
		JPH_PhysicsSystem_Update(s->system, 1.0f / 11.0f, 1, jobs);
	}
}

static void SceneDestroy(Scene* s)
{
	JPH_BodyInterface* bi = JPH_PhysicsSystem_GetBodyInterface(s->system);
	for (int i = 0; i < 100; i++)
		JPH_BodyInterface_RemoveAndDestroyBody(bi, s->ids[i]);
	JPH_BodyInterface_RemoveAndDestroyBody(bi, s->floorId);
	JPH_Shape_Destroy(s->box);
	JPH_Shape_Destroy(s->floor);
	/* joltc's JPH_PhysicsSystem_Destroy deletes the three layer objects too; the listener outlives the system. */
	JPH_PhysicsSystem_Destroy(s->system);
	if (s->listener)
		JPH_ContactListener_Destroy(s->listener);
}

int main(int argc, char** argv)
{
	if (argc < 3)
	{
		fprintf(stderr, "usage: leakcheck validate|novalidate|nolistener|restart <count>\n");
		return 2;
	}
	const char* mode = argv[1];
	int count = atoi(argv[2]);

	if (!JPH_Init())
		return 1;
	JobSystemThreadPoolConfig config = { 2048, 8, 2 };
	JPH_JobSystem* jobs = JPH_JobSystemThreadPool_Create(&config);

	s_procs.OnContactValidate = strcmp(mode, "novalidate") == 0 || strcmp(mode, "restart") == 0 ? NULL : OnValidate;
	s_procs.OnContactAdded = OnAdded;
	s_procs.OnContactPersisted = OnAdded;
	s_procs.OnContactRemoved = OnRemoved;
	JPH_ContactListener_SetProcs(&s_procs);

	Scene s;
	if (strcmp(mode, "restart") == 0)
	{
		for (int c = 0; c < count; c++)
		{
			SceneCreate(&s, 1, 1);
			SceneStep(&s, jobs, 20);
			SceneDestroy(&s);
		}
	}
	else
	{
		SceneCreate(&s, strcmp(mode, "nolistener") != 0, 0);
		SceneStep(&s, jobs, count);
		SceneDestroy(&s);
	}
	printf("LEAKCHECK mode=%s count=%d validates=%ld\n", mode, count, s_validates);

	JPH_JobSystem_Destroy(jobs);
	JPH_Shutdown();
	return 0;
}
