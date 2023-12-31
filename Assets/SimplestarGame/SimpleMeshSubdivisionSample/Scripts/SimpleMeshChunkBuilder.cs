﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static SimplestarGame.CAWFile;
using UnityEngine.Rendering;

namespace SimplestarGame
{
    public class SimpleMeshChunkBuilder
    {
        CubeData cubeData;
        NativeArray<byte> voxelData;
        List<IndexXYZ> levelDataList;
        Material material;
        Color[] levelColors;

        public SimpleMeshChunkBuilder(CubeData cubeData, NativeArray<byte> voxelData, List<IndexXYZ> levelDataList, Material material, Color[] levelColors)
        {
            this.cubeData = cubeData;
            this.voxelData = voxelData;
            this.levelDataList = levelDataList;
            this.material = material;
            this.levelColors = levelColors;
        }

        public async Task CreateChunkMesh(List<GameObject> meshObjectList, SimpleMeshChunk chunk, Vector3[] interactPoints, bool async)
        {
            if (chunk.chunkLevel > ChunkLevel.Cube1 && this.HasInteractPoints(chunk, interactPoints))
            {
                // 分割可能ならば分割構築する
                var createChildrenTask = this.CreateChunkChildren(chunk, interactPoints, async);
                if (async)
                {
                    await createChildrenTask;
                }
                else
                {
                    createChildrenTask.Wait();
                }
                chunk.DestroyMesh();
            }
            else if (chunk.meshObject == null)
            {
                // このチャンク用メッシュオブジェクトを作成
                GameObject newMeshObject = null;
                var task = this.CreateChunkObject(this.voxelData, this.levelDataList, this.cubeData, chunk.offset, chunk.chunkLevel, async);
                if (async)
                {
                    newMeshObject = await task;
                }
                else
                {
                    task.Wait();
                    newMeshObject = task.Result;
                }
                if (newMeshObject != null)
                {
                    newMeshObject.transform.SetParent(chunk.transform, false);
                    chunk.meshObject = newMeshObject;
                    meshObjectList.Add(newMeshObject);
                }
                chunk.DestroyChildren();
            }
        }


        async Task CreateChunkChildren(SimpleMeshChunk parentChunk, Vector3[] interactPoints, bool async)
        {
            var myChunkLevel = parentChunk.chunkLevel - 1;
            List<SimpleMeshChunk> chunks = CreateChildrenChunks(parentChunk, myChunkLevel);
            List<GameObject> meshObjectList = new List<GameObject>();
            foreach (var chunk in chunks)
            {
                var createChunkMeshTask = this.CreateChunkMesh(meshObjectList, chunk, interactPoints, myChunkLevel > ChunkLevel.Cube16);
                if (async)
                {
                    await createChunkMeshTask;
                }
                else
                {
                    createChunkMeshTask.Wait();
                }
            }
            var buildMeshTask = this.BakeMeshAsync(meshObjectList, async);
            if (async)
            {
                await buildMeshTask;
            }
            else
            {
                buildMeshTask.Wait();
            }
            parentChunk.children = chunks;
        }

        static List<SimpleMeshChunk> CreateChildrenChunks(SimpleMeshChunk parentChunk, ChunkLevel myChunkLevel)
        {
            var edgeCubes = SimpleMeshChunk.levelEdgeCubes[(int)myChunkLevel];
            List<SimpleMeshChunk> chunks = parentChunk.children;
            if (chunks == null)
            {
                chunks = new List<SimpleMeshChunk>();
                for (int chunkX = 0; chunkX < 2; chunkX++)
                {
                    for (int chunkY = 0; chunkY < 2; chunkY++)
                    {
                        for (int chunkZ = 0; chunkZ < 2; chunkZ++)
                        {
                            var chunkOffset = new Vector3Int(chunkX, chunkY, chunkZ) * edgeCubes;
                            var newGameObject = new GameObject($"{chunkX}, {chunkY}, {chunkZ}");
                            newGameObject.transform.SetParent(parentChunk.transform, false);
                            newGameObject.transform.localPosition = chunkOffset;
                            newGameObject.isStatic = true;
                            var chunk = newGameObject.AddComponent<SimpleMeshChunk>();
                            chunk.SetData(myChunkLevel, parentChunk.offset + chunkOffset);
                            chunks.Add(chunk);
                        }
                    }
                }
            }
            return chunks;
        }

        /// <summary>
        /// チャンク内に興味ポイントを持つかどうか
        /// </summary>
        /// <param name="chunk">チャンク情報</param>
        /// <returns>興味ポイントを持つ場合 true</returns>
        bool HasInteractPoints(SimpleMeshChunk chunk, Vector3[] interactPoints)
        {
            bool isInInteractPoint = false;
            foreach (var interactPoint in interactPoints)
            {
                isInInteractPoint = math.all(new float3(chunk.minBounds) < new float3(interactPoint)) &&
                    math.all(new float3(chunk.maxBounds) > new float3(interactPoint));
                if (isInInteractPoint)
                {
                    break;
                }
            }
            return isInInteractPoint;
        }        

        /// <summary>
        /// メッシュの作成
        /// </summary>
        /// <param name="meshDataArray">データ設定済みメッシュデータ</param>
        /// <param name="vertexIndexCount">インデックス数=頂点数</param>
        /// <param name="bounds">バウンディングボックス情報</param>
        /// <returns>作成したメッシュ</returns>
        async Task<Mesh> CreateMesh(Mesh.MeshDataArray meshDataArray, int vertexIndexCount, float3x2 bounds, bool async)
        {
            var newMesh = new Mesh();
            newMesh.name = "CustomLayoutMesh";
            var meshBounds = newMesh.bounds = new Bounds((bounds.c0 + bounds.c1) * 0.5f, bounds.c1 - bounds.c0);
            var task = Task.Run(() => {
                meshDataArray[0].SetSubMesh(0, new SubMeshDescriptor
                {
                    topology = MeshTopology.Triangles,
                    vertexCount = vertexIndexCount,
                    indexCount = vertexIndexCount,
                    baseVertex = 0,
                    firstVertex = 0,
                    indexStart = 0,
                    bounds = meshBounds
                }, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
            });
            if (async)
            {
                await task;
            }
            else
            {
                task.Wait();
            }
            Mesh.ApplyAndDisposeWritableMeshData(meshDataArray, new[] { newMesh },
                    MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers);
            return newMesh;
        }

        /// <summary>
        /// メッシュオブジェクト作成
        /// </summary>
        /// <param name="worldData">ワールド全体データ</param>
        /// <param name="cubeData">キューブの頂点情報</param>
        /// <param name="chunkOffset">ワールド内のローカル塊オフセット</param>
        /// <param name="chunkLevel">塊の辺キューブ数</param>
        /// <returns>作成したゲームオブジェクト</returns>
        async Task<GameObject> CreateChunkObject(
            NativeArray<byte> voxelData,
            List<IndexXYZ> levelDataList,
            CubeData cubeData,
            Vector3Int chunkOffset,
            ChunkLevel chunkLevel,
            bool async)
        {
            // カウント
            var countTask = Task.Run(() => {
                var countJobHandle = new CountChunkVertexJob()
                {
                    vertexCounts = cubeData.vertexCounts,
                    voxelData = voxelData,
                    xyz = levelDataList[(int)chunkLevel].xyz,
                    results = levelDataList[(int)chunkLevel].countOffsets,
                    chunkOffset = chunkOffset,
                    dataEdgeCubeCount = SimpleMeshChunk.dataEdgeCubeCount,
                }.Schedule(levelDataList[(int)chunkLevel].xyz.Length, 8);
                countJobHandle.Complete();
            });
            if (async)
            {
                await countTask;
            }
            else
            {
                countTask.Wait();
            }
            // 集計
            int vertexIndexCount = 0;
            var sumTask = Task.Run(() =>
            {
                int vertexIndexCount = 0;
                for (int index = 0; index < levelDataList[(int)chunkLevel].countOffsets.Length; index++)
                {
                    var counts = levelDataList[(int)chunkLevel].countOffsets[index];
                    levelDataList[(int)chunkLevel].countOffsets[index] = vertexIndexCount;
                    vertexIndexCount += counts;
                }
                return vertexIndexCount;
            });
            if (async)
            {
                vertexIndexCount = await sumTask;
            }
            else
            {
                sumTask.Wait();
                vertexIndexCount = sumTask.Result;
            }
            if (vertexIndexCount == 0)
            {
                return null;
            }
            // 確保
            var meshDataArray = Mesh.AllocateWritableMeshData(1);
            Mesh.MeshData meshData = meshDataArray[0];
            meshData.subMeshCount = 1;
            meshData.SetVertexBufferParams(vertexIndexCount, CustomLayoutMesh.VERTEX_ATTRIBUTE_DESCRIPTORS);
            meshData.SetIndexBufferParams(vertexIndexCount, IndexFormat.UInt32);
            NativeArray<int> indexData = meshData.GetIndexData<int>();
            // インデックス書き込み
            var indexJobHandle = new WriteIndexDataJob() { indexData = indexData }.Schedule(indexData.Length, 128);
            indexJobHandle.Complete();
            // 頂点データ書き込み
            NativeArray<CustomVertexLayout> vertexData = meshData.GetVertexData<CustomVertexLayout>(stream: 0);
            var writeTask = Task.Run(() => {
                var writeJobHandle = new WriteChunkDataJob()
                {
                    vertexCounts = cubeData.vertexCounts,
                    voxelData = voxelData,
                    xyz = levelDataList[(int)chunkLevel].xyz,
                    countOffsets = levelDataList[(int)chunkLevel].countOffsets,
                    fileVertexData = cubeData.fileVertexData,
                    vertexData = vertexData,
                    chunkOffset = chunkOffset,
                    dataEdgeCubeCount = SimpleMeshChunk.dataEdgeCubeCount,
                }.Schedule(levelDataList[(int)chunkLevel].xyz.Length, 8);
                writeJobHandle.Complete();
            });
            if (async)
            {
                await writeTask;
            }
            else
            {
                writeTask.Wait();
            }
            // バウンディングボックス
            float3x2 bounds = new float3x2();
            bounds.c0 = math.min(bounds.c0, new float3(-0.5f, -0.5f, -0.5f));
            var edgeCubeCount = SimpleMeshChunk.levelEdgeCubes[(int)chunkLevel];
            bounds.c1 = math.max(bounds.c1, new float3(edgeCubeCount + 0.5f, edgeCubeCount + 0.5f, edgeCubeCount + 0.5f));
            // オブジェクト作成
            Mesh newMesh = null;
            var createMeshTask = this.CreateMesh(meshDataArray, vertexIndexCount, bounds, async);
            if (async)
            {
                newMesh = await createMeshTask;
            }
            else
            {
                createMeshTask.Wait();
                newMesh = createMeshTask.Result;
            }
            vertexData.Dispose();
            indexData.Dispose();

            var newGameObject = new GameObject("TestCubeMeshObject" + chunkLevel.ToString());
            newGameObject.AddComponent<MeshFilter>().sharedMesh = newMesh;
            var material = new Material(this.material);
            material.color = this.levelColors[(int)chunkLevel];
            newGameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
            return newGameObject;
        }

        /// <summary>
        /// MeshCollider 作成
        /// </summary>
        /// <param name="meshObjectList">MeshFilter の sharedMesh を入力に MeshCollider を計算します</param>
        /// <returns>Task</returns>
        async Task BakeMeshAsync(List<GameObject> meshObjectList, bool async)
        {
            var meshIds = new NativeArray<int>(meshObjectList.Count, Allocator.Persistent);
            var meshIdx = 0;
            foreach (var meshObject in meshObjectList)
            {
                var mesh = meshObject.GetComponent<MeshFilter>().sharedMesh;
                meshIds[meshIdx++] = mesh.GetInstanceID();
            }
            var task = Task.Run(() =>
            {
                var bakeMeshJob = new BakeMeshJob(meshIds);
                var bakeMeshJobHandle = bakeMeshJob.Schedule(meshIds.Length, 1);
                bakeMeshJobHandle.Complete();
                meshIds.Dispose();
            });
            if (async)
            {
                await task;
            }
            else
            {
                task.Wait();
            }
            // Set MeshCollider
            foreach (var meshObject in meshObjectList)
            {
                meshObject.AddComponent<MeshCollider>().sharedMesh = meshObject.GetComponent<MeshFilter>().sharedMesh;
            }
        }
    }
}
