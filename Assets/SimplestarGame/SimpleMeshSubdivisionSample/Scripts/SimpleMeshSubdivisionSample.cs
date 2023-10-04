using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using static SimplestarGame.CAWFile;

namespace SimplestarGame
{
    public class IndexXYZ
    {
        public NativeArray<XYZ> xyz;
        public NativeArray<int> countOffsets;
    }

    public class SimpleMeshSubdivisionSample : MonoBehaviour
    {
        [SerializeField] string meshFileName = "BasicCube.caw";
        [SerializeField] string dataFileName = "world000.gz";
        
        [SerializeField] Material material;
        [SerializeField] Transform[] parents;
        [SerializeField] Transform[] interactPoints;
        [SerializeField] Color[] levelColors = new Color[] { 
            Color.white * 0.2f, 
            Color.white * 0.3f, 
            Color.white * 0.4f, 
            Color.white * 0.5f, 
            Color.white * 0.6f, 
            Color.white * 0.7f, 
            Color.white * 0.8f, 
            Color.white * 0.9f,
            Color.white * 0.95f, 
            Color.white };

        /// <summary>
        /// ���[���h���\������ő嗱�x�`�����N
        /// </summary>
        List<SimpleMeshChunk> chunks = new List<SimpleMeshChunk>();
        SimpleMeshChunkBuilder chunkBuilder;

        CubeData cubeData;
        NativeArray<byte> voxelData;
        List<IndexXYZ> levelDataList;

        Task createMeshesTask = null;
        bool cancelCreateMeshes = false;

        float timer = 0f;
        float interval = 10f; // ���������s����Ԋu�i�b�j

        void Awake()
        {
            Application.targetFrameRate = 30;
            GraphicsSettings.useScriptableRenderPipelineBatching = true;
        }

        async void Start()
        {
            // �L���[�u�̃��b�V���f�[�^�\�[�X��ǂݍ���
            this.cubeData = await Task.Run(() => CAWFile.ReadCAWFile(Path.Combine(Application.streamingAssetsPath, this.meshFileName)));
            // ���[���h�̃{�N�Z���f�[�^�ǂݍ���
            var compressedData = await Task.Run(() => File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, this.dataFileName)));
            // ���[���h�̃f�[�^��
            var worldDataBytes = await Task.Run(() => GZipCompressor.Unzip(compressedData));
            this.voxelData = await Task.Run(() => new NativeArray<byte>(worldDataBytes, Allocator.Persistent));
            // Job�pNativeArray�m��
            this.levelDataList = await AllocateDataAsync();
            // �r���_�[��������
            this.chunkBuilder = new SimpleMeshChunkBuilder(this.cubeData, this.voxelData, this.levelDataList, this.material, this.levelColors);

            // �`�����N�I�u�W�F�N�g���쐬�A���X�g��
            this.chunks.Clear();
            foreach (var parent in this.parents)
            {
                var myChunkLevel = ChunkLevel.Cube256;
                var edgeCubes = SimpleMeshChunk.levelEdgeCubes[(int)myChunkLevel];
                for (int chunkX = 0; chunkX < 1; chunkX++)
                {
                    for (int chunkY = 0; chunkY < 1; chunkY++)
                    {
                        for (int chunkZ = 0; chunkZ < 1; chunkZ++)
                        {
                            var chunkOffset = new Vector3Int(chunkX, chunkY, chunkZ) * edgeCubes;
                            var newGameObject = new GameObject($"{chunkX}, {chunkY}, {chunkZ}");
                            newGameObject.transform.SetParent(parent.transform, false);
                            newGameObject.transform.localPosition = chunkOffset;
                            newGameObject.isStatic = true;
                            var chunk = newGameObject.AddComponent<SimpleMeshChunk>();
                            chunk.SetData(myChunkLevel, chunkOffset);
                            this.chunks.Add(chunk);
                        }
                    }
                }
            }
            Vector3[] interactPoints = new Vector3[this.interactPoints.Length];
            for (int i = 0; i < this.interactPoints.Length; i++)
            {
                interactPoints[i] = this.interactPoints[i].position;
            }
            this.createMeshesTask = this.CreateWorldMeshes(this.chunks, interactPoints);
        }

        void OnDestroy()
        {
            // �m�ۂ������̂��J��
            foreach (var levelData in this.levelDataList)
            {
                levelData.countOffsets.Dispose();
                levelData.xyz.Dispose();
            }
            this.voxelData.Dispose();
            this.cubeData.fileVertexData.Dispose();
            this.cubeData.vertexCounts.Dispose();
        }

        async void Update()
        {
            // �^�C�}�[���X�V
            this.timer += Time.deltaTime;

            // �^�C�}�[���w�肵���Ԋu�𒴂����ꍇ�ɏ��������s
            if (this.timer >= this.interval)
            {
                await this.ReBuildMesh();

                // �^�C�}�[�����Z�b�g
                this.timer = 0f;
            }

            // Space �L�[�������ƁA�����|�C���g�t�߂̃��b�V�����č\�z
            if (Input.GetKeyDown(KeyCode.Space))
            {
                await this.ReBuildMesh();
            }
        }

        async Task ReBuildMesh()
        {
            while (this.createMeshesTask != null && !this.createMeshesTask.IsCompleted)
            {
                this.cancelCreateMeshes = true;
                await Task.Delay(100);
            }
            Vector3[] interactPoints = new Vector3[this.interactPoints.Length];
            for (int i = 0; i < this.interactPoints.Length; i++)
            {
                interactPoints[i] = this.interactPoints[i].position;
            }
            this.createMeshesTask = this.CreateWorldMeshes(this.chunks, interactPoints);
        }

        /// <summary>
        /// �`�����N�����ԂɃ��b�V���I�u�W�F�N�g��
        /// </summary>
        /// <param name="chunks">�\�[�g���ꂽ�`�����N�ꗗ</param>
        /// <param name="interactPoints">�����|�C���g���W�ꗗ</param>
        /// <returns>async Task</returns>
        async Task CreateWorldMeshes(List<SimpleMeshChunk> chunks, Vector3[] interactPoints)
        {
            this.cancelCreateMeshes = false;
            // ���݂̃`�����N�ꗗ���J�����ɋ߂����Ƀ\�[�g
            var mainCamera = Camera.main.transform;
            await this.SortChunksAsync(chunks, mainCamera.position, mainCamera.forward);
            List<GameObject> meshObjectList = new List<GameObject>();
            foreach (var chunk in chunks)
            {
                if (this.cancelCreateMeshes)
                {
                    break;
                }
                if (chunk.dot < 0f && 256 < chunk.distance)
                {
                    // �\���s�v�Ȃ��̂̓X�L�b�v
                    continue;
                }
                await this.chunkBuilder.CreateChunkMesh(meshObjectList, chunk, interactPoints, true);
            }
        }

        async Task SortChunksAsync(List<SimpleMeshChunk> chunks, Vector3 viewPoint, Vector3 viewDirection)
        {
            await Task.Run(() => { SortChunks(chunks, viewPoint, viewDirection); });
        }

        static void SortChunks(List<SimpleMeshChunk> chunks, Vector3 viewPoint, Vector3 viewDirection)
        {
            var points = new NativeArray<float3>(chunks.Count, Allocator.Persistent);
            var cameraDistances = new NativeArray<DotDistance>(chunks.Count, Allocator.Persistent);
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                points[i] = chunk.center;
            }
            var calculateCameraDistanceJob = new CalculateDotDistanceJob()
            {
                points = points,
                viewPoint = viewPoint,
                viewDirection = viewDirection,
                dotDistances = cameraDistances
            };
            calculateCameraDistanceJob.Schedule(points.Length, 1).Complete();
            for (int i = 0; i < cameraDistances.Length; i++)
            {
                var cameraDistance = cameraDistances[i];
                chunks[i].distance = cameraDistance.distance;
                chunks[i].dot = cameraDistance.dot;
            }
            cameraDistances.Dispose();
            points.Dispose();
            chunks.Sort((a, b) => a.distance > b.distance ? 1 : -1);
        }

        /// <summary>
        /// �v�Z�Ŗ���g���o�b�t�@�A�g���܂킷���߂ɍŏ��Ɋm��
        /// </summary>
        /// <returns>�m�ۂ����o�b�t�@</returns>
        static async Task<List<IndexXYZ>> AllocateDataAsync()
        {
            return await Task.Run(() => {
                List<IndexXYZ> levelDataList = new List<IndexXYZ>();
                for (ChunkLevel chunkLevel = ChunkLevel.Cube1; chunkLevel <= ChunkLevel.Cube256; chunkLevel++)
                {
                    var edgeCubeCount = SimpleMeshChunk.levelEdgeCubes[(int)chunkLevel];
                    var size = edgeCubeCount * edgeCubeCount * edgeCubeCount;
                    var xyz = new NativeArray<XYZ>(size, Allocator.Persistent);
                    var countOffsets = new NativeArray<int>(size, Allocator.Persistent);
                    for (int x = 0; x < edgeCubeCount; x++)
                    {
                        for (int y = 0; y < edgeCubeCount; y++)
                        {
                            for (int z = 0; z < edgeCubeCount; z++)
                            {
                                var index = x * edgeCubeCount * edgeCubeCount + y * edgeCubeCount + z;
                                xyz[index] = new XYZ { x = (byte)x, y = (byte)y, z = (byte)z };
                                countOffsets[index] = 0;
                            }
                        }
                    }
                    levelDataList.Add(new IndexXYZ { xyz = xyz, countOffsets = countOffsets });
                }
                return levelDataList;
            });
        }
    }
}