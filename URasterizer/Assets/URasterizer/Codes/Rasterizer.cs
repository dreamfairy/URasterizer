using System;
using System.Collections.Generic;
using UnityEngine;

namespace URasterizer
{
    struct OutBuf
    {
        public Vector4 clipPos;
        public Vector3 worldPos;
        public Vector3 objectNormal;
        public Vector3 worldNormal;
    }

    public enum BufferMask
    {
        Color = 1,
        Depth = 2
    }    

    public delegate void OnRasterizerStatUpdate(int verticesAll, int trianglesAll, int trianglesRendered);

    public class Rasterizer
    {
        int _width;
        int _height;

        RenderingConfig _config;

        Matrix4x4 _matModel;
        Matrix4x4 _matView;
        Matrix4x4 _matProjection;

        public Matrix4x4 ModelMatrix
        {
            get => _matModel;           
            set => _matModel = value;            
        }

        public Matrix4x4 ViewMatrix
        {
            get => _matView;
            set => _matView = value;
        }

        public Matrix4x4 ProjectionMatrix
        {
            get => _matProjection;
            set => _matProjection = value;
        }

        Color[] frame_buf;
        float[] depth_buf;
        Color[] temp_buf;

        Color[] samplers_color_MSAA;
        bool[] samplers_mask_MSAA;
        float[] samplers_depth_MSAA;

        public Texture2D texture;

        public FragmentShader CurrentFragmentShader;

        //Stats
        int _trianglesAll, _trianglesRendered;
        int _verticesAll;

        public OnRasterizerStatUpdate StatDelegate;


        public Rasterizer(int w, int h, RenderingConfig config)
        {
            Debug.Log($"Rasterizer screen size: {w}x{h}");

            _config = config;

            _width = w;
            _height = h;

            frame_buf = new Color[w * h];
            depth_buf = new float[w * h];
            temp_buf = new Color[w * h];

            texture = new Texture2D(w, h);
            texture.filterMode = FilterMode.Point;

            if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                AllocateMSAABuffers();
            }
        }

        void AllocateMSAABuffers()
        {
            int MSAALevel = (int)_config.MSAA;
            int bufSize = _width * _height * MSAALevel * MSAALevel;
            if(samplers_color_MSAA==null || samplers_color_MSAA.Length != bufSize)
            {
                samplers_color_MSAA = new Color[bufSize];
                samplers_mask_MSAA = new bool[bufSize];
                samplers_depth_MSAA = new float[bufSize];
            }            
        }

        public float Aspect
        {
            get
            {
                return (float)_width / _height;
            }
        }

        void FillArray<T>(T[] arr, T value)
        {
            int i = 0;
            if(arr.Length > 16)
            {
                do
                {
                    arr[i++] = value;
                } while (i < arr.Length);

                while( i + 16 < arr.Length)
                {
                    Array.Copy(arr, 0, arr, i, 16);
                    i += 16;
                }
            }
            while (i < arr.Length)
            {
                arr[i++] = value;
            }
        }

        public void Clear(BufferMask mask)
        {
            if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                AllocateMSAABuffers();
            }

            if ((mask & BufferMask.Color) == BufferMask.Color)
            {                
                FillArray(frame_buf, _config.ClearColor);
                if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
                {
                    FillArray(samplers_color_MSAA, _config.ClearColor);
                    FillArray(samplers_mask_MSAA, false);
                }
            }
            if((mask & BufferMask.Depth) == BufferMask.Depth)
            {
                FillArray(depth_buf, 0f);
                if (_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
                {
                    FillArray(samplers_depth_MSAA, 0f);
                }
            }

            _trianglesAll = _trianglesRendered = 0;
            _verticesAll = 0;

            
        }

        public void SetupViewProjectionMatrix(Camera camera)
        {
            //��������ϵת��������ϵ,�������������zȡ��
            var camPos = camera.transform.position;
            camPos.z *= -1; 
            var lookAt = camera.transform.forward;
            lookAt.z *= -1;
            var up = camera.transform.up;
            up.z *= -1;
            
            ViewMatrix = TransformTool.GetViewMatrix(camPos, lookAt, up);

            if (camera.orthographic)
            {
                float halfOrthHeight = camera.orthographicSize;
                float halfOrthWidth = halfOrthHeight * Aspect;
                float f = -camera.farClipPlane;
                float n = -camera.nearClipPlane;
                ProjectionMatrix = TransformTool.GetOrthographicProjectionMatrix(-halfOrthWidth, halfOrthWidth, -halfOrthHeight, halfOrthHeight, f, n);
            }
            else
            {
                ProjectionMatrix = TransformTool.GetPerspectiveProjectionMatrix(camera.fieldOfView, Aspect, camera.nearClipPlane, camera.farClipPlane);
            }
        }

        public void Draw(RenderingObject ro, Camera camera)
        {
            Mesh mesh = ro.mesh;
            SetupViewProjectionMatrix(camera);

            ModelMatrix = ro.GetModelMatrix();                      

            Matrix4x4 mvp = _matProjection * _matView * _matModel;
            Matrix4x4 normalMat = _matModel.inverse.transpose;

            _verticesAll += mesh.vertexCount;
            _trianglesAll += mesh.triangles.Length / 3;

            //Unityģ�ͱ�������ϵҲ������ϵ����Ҫת������ʹ�õ�����ϵ
            //1. z�ᷴת
            //2. �����ζ��㻷�Ʒ����˳ʱ��ĳ���ʱ��


            /// ------------- Vertex Shader -------------------
            Vector4[] csVertices = new Vector4[mesh.vertexCount]; //clip space vertices
            Vector3[] wsVertices = new Vector3[mesh.vertexCount]; //world space vertices
            Vector3[] osNormals = new Vector3[mesh.vertexCount]; //obj space normals
            Vector3[] wsNormals = new Vector3[mesh.vertexCount]; //world space normals

            if(_config.UseComputeShader && _config.VertexShader != null){
                ComputeBuffer vertexBuffer = new ComputeBuffer(mesh.vertexCount, 3*4);
                vertexBuffer.SetData(mesh.vertices);
                ComputeBuffer normalBuffer = new ComputeBuffer(mesh.vertexCount, 3*4);
                normalBuffer.SetData(mesh.normals);
                ComputeBuffer outBuffer = new ComputeBuffer(mesh.vertexCount, 13*4);
                

                var shader = _config.VertexShader;
                int kernel = shader.FindKernel("CSMain");
                shader.SetMatrix("matMVP", mvp);
                shader.SetMatrix("matModel", _matModel);
                shader.SetBuffer(kernel, "vertexBuffer", vertexBuffer);
                shader.SetBuffer(kernel,"normalBuffer", normalBuffer);
                shader.SetBuffer(kernel, "outBuffer", outBuffer);
                
                int groupCnt = mesh.vertexCount/256;
                groupCnt = groupCnt==0? 1: groupCnt;
                shader.Dispatch(kernel, groupCnt, 1, 1);  

                OutBuf[] output = new OutBuf[mesh.vertexCount];
                outBuffer.GetData(output);  

                for(int i=0; i<mesh.vertexCount; ++i)
                {
                    csVertices[i] = output[i].clipPos;
                    wsVertices[i] = output[i].worldPos;
                    osNormals[i] = output[i].objectNormal;
                    wsNormals[i] = output[i].worldNormal;
                }                
                    
            }
            else{
                for(int i=0; i<mesh.vertexCount; ++i)
                {                
                    var vert = mesh.vertices[i];        
                    var objVert = new Vector4(vert.x, vert.y, -vert.z, 1); //ע�������ת��z����
                    csVertices[i] = mvp * objVert;
                    wsVertices[i] = _matModel * objVert;
                    var normal = mesh.normals[i];
                    var objNormal = new Vector3(normal.x, normal.y, -normal.z);
                    osNormals[i] = objNormal;
                    wsNormals[i] = _matModel * objNormal;
                }
            }

            


            var indices = mesh.triangles;
            for(int i=0; i< indices.Length; i+=3)
            {         
                /// -------------- Primitive Assembly -----------------

                //ע������Ե���v0��v1����������Ϊԭ���� 0,1,2��˳ʱ��ģ��Ե����� 1,0,2����ʱ���
                //Unity Quardģ�͵����������������ֱ��� 0,3,1,3,0,2 ת����Ϊ 3,0,1,0,3,2
                int idx0 = indices[i+1];
                int idx1 = indices[i]; 
                int idx2 = indices[i+2];
                             
                Vector4[] v =
                {
                    csVertices[idx0],
                    csVertices[idx1],
                    csVertices[idx2]                   
                };                
                

                // ------ Clipping -------
                if (Clipped(v))
                {
                    continue;
                }

                // ------- Perspective division --------
                //clip space to NDC
                for (int k=0; k<3; k++)
                {
                    v[k].x /= v[k].w;
                    v[k].y /= v[k].w;
                    v[k].z /= v[k].w;                  
                }

                //backface culling
                if (_config.BackfaceCulling && !ro.DoubleSideRendering)
                {
                    Vector3 v0 = new Vector3(v[0].x, v[0].y, v[0].z);
                    Vector3 v1 = new Vector3(v[1].x, v[1].y, v[1].z);
                    Vector3 v2 = new Vector3(v[2].x, v[2].y, v[2].z);
                    Vector3 e01 = v1 - v0;
                    Vector3 e02 = v2 - v0;
                    Vector3 cross = Vector3.Cross(e01, e02);
                    if (cross.z < 0)
                    {
                        continue;
                    }
                }

                ++_trianglesRendered;

                // ------- Viewport Transform ----------
                //NDC to screen space
                for (int k = 0; k < 3; k++)
                {
                    var vec = v[k];
                    vec.x = 0.5f * _width * (vec.x + 1.0f);
                    vec.y = 0.5f * _height * (vec.y + 1.0f);

                    //��Ӳ����Ⱦ�У�NDC��zֵ����Ӳ����͸�ӳ���֮���ֱ��д�뵽depth buffer�ˣ����Ҫ������Ҫ��ͶӰ�����е���
                    //���������������Ⱦ�����Կ������������zֵ��                    

                    //GAMES101Լ����NDC����������ϵ��zֵ��Χ��[-1,1]����nΪ1��fΪ-1�����ֵԽ��Խ����n��                    
                    //Ϊ�˿��ӻ�Depth buffer�������յ�zֵ��[-1,1]ӳ�䵽[0,1]�ķ�Χ���������nΪ1, fΪ0����nԽ�������ֵԽ��                    
                    //����Զ����zֵΪ0�����clearʱ���Ҫ���Ϊ0��Ȼ����Ȳ���ʱ��ʹ��GREATER���ԡ�
                    //(��Ȼ����Ҳ�����������תzֵ��Ȼ��clearʱʹ��float.MaxValue�����������Ȳ���ʱʹ��LESS_EQUAL����)
                    //ע�⣺�����zֵ���������Ǳ�Ҫ�ģ�ֻ��Ϊ�˿��ӻ�ʱ����ӳ��Ϊ��ɫֵ����ʵҲ�����ڿ��ӻ��ĵط�������
                    //������ô���������ú�Unity��DirectXƽ̨��Reverse zһ������near plane������zֵ�ĸ�����������ߡ�
                    vec.z = vec.z * 0.5f + 0.5f; 

                    v[k] = vec;
                }

                Triangle t = new Triangle();
                for(int k=0; k<3; k++)
                {
                    t.SetPosition(k, v[k]);                       
                }

                //set obj normal
                t.SetNormal(0, osNormals[idx0]);
                t.SetNormal(1, osNormals[idx1]);
                t.SetNormal(2, osNormals[idx2]);

                if (mesh.uv.Length > 0)
                {
                    Vector2[] uv =
                    {
                        mesh.uv[idx0],
                        mesh.uv[idx1],
                        mesh.uv[idx2]
                    };
                    for (int k = 0; k < 3; k++)
                    {                        
                        t.SetTexCoord(k, uv[k]);
                    }
                }

                //���ö���ɫ,ʹ��config�е���ɫ����ѭ������                
                if(_config.VertexColors != null && _config.VertexColors.Colors.Length > 0)
                {
                    int vertexColorCount = _config.VertexColors.Colors.Length;

                    t.SetColor(0, _config.VertexColors.Colors[idx0 % vertexColorCount]);
                    t.SetColor(1, _config.VertexColors.Colors[idx1 % vertexColorCount]);
                    t.SetColor(2, _config.VertexColors.Colors[idx2 % vertexColorCount]);
                }
                else
                {
                    t.SetColor(0, Color.white);
                    t.SetColor(1, Color.white);
                    t.SetColor(2, Color.white);
                }

                //set world space pos & normal
                t.SetWorldPos(0, wsVertices[idx0]);
                t.SetWorldPos(1, wsVertices[idx1]);
                t.SetWorldPos(2, wsVertices[idx2]);
                t.SetWorldNormal(0, wsNormals[idx0]);
                t.SetWorldNormal(1, wsNormals[idx1]);
                t.SetWorldNormal(2, wsNormals[idx2]);

                /// ---------- Rasterization -----------
                if (_config.WireframeMode)
                {
                    RasterizeWireframe(t);
                }
                else
                {
                    RasterizeTriangle(t, ro);
                }
                
            }

            //Resolve AA
            if(_config.MSAA != MSAALevel.Disabled && !_config.WireframeMode)
            {
                int MSAALevel = (int)_config.MSAA;
                int SamplersPerPixel = MSAALevel * MSAALevel;

                for (int y=0; y < _height; ++y)
                {
                    for(int x=0; x < _width; ++x)
                    {
                        int index = GetIndex(x, y);
                        Color color = Color.clear;
                        float a = 0.0f;
                        for(int si=0; si < MSAALevel; ++si)
                        {
                            for(int sj=0; sj < MSAALevel; ++sj)
                            {
                                int xi = x * MSAALevel + si;
                                int yi = y * MSAALevel + sj;
                                int indexSamper = yi * _width * MSAALevel + xi;
                                if (samplers_mask_MSAA[indexSamper])
                                {
                                    color += samplers_color_MSAA[indexSamper];
                                    a += 1.0f;
                                }
                            }
                        }
                        if(a > 0.0f)
                        {
                            frame_buf[index] = color / SamplersPerPixel;
                        }
                    }
                }
            }
        }        

        //������Clipping���������ڲ�����clipping volume�е�ͼԪ��
        //Ӳ��ʵ��ʱһ��ֻ�Բ��ֶ���zֵ��near,far֮���ͼԪ����clipping������
        //�����ֶ���x,yֵ��x,y�ü�ƽ��֮���ͼԪ�򲻽��вü���ֻ��ͨ��һ����viewport����һЩ��guard-band������������޳����൱�ڷŴ�x,y�Ĳ��Է�Χ��
        //����x,y�ü�ƽ��֮���ͼԪ������frame buffer�Ͻ���Scissor���ԡ�
        //�˴���ʵ�ּ�Ϊֻ�����޳��������κ�clipping����������x,y�ü�û���⣬��Ȼû����region,Ҳ���������frame buffer�ϲü�����
        //����z�Ĳü�����û�д����ῴ��������������ʧ���µı�Ե������
        bool Clipped(Vector4[] v)
        {
            //Clip spaceʹ��GAMES101�淶����������ϵ��nΪ+1�� fΪ-1
            //�ü����������޳���     
            //ʵ�ʵ�Ӳ������Clip space�ü������Դ˴�����Ҳʹ��clip space ����Ȼ�������ǲ������Ĳü���ֻ�������޳���������ʵ��NDC���������㣩
            for (int i = 0; i < 3; ++i)
            {
                var vertex = v[i];
                var w = vertex.w;
                w = w >= 0 ? w : -w; //����NDC����������-1<=Zndc<=1, ���� w < 0 ʱ��-w >= Zclip = Zndc*w >= w�����Դ�ʱclip space�����귶Χ��[w,-w], Ϊ�˱Ƚ�ʱ����ȷ����wȡ��
                
                bool inside = (vertex.x <= w && vertex.x >= -w
                    && vertex.y <= w && vertex.y >= -w
                    && vertex.z <= w && vertex.z >= -w);
                if (inside)
                {             
                    //���ü������Σ�ֻҪ������һ����clip space�������������屣��
                    return false;
                }
            }

            //�������㶼���������������޳�
            return true;
        }

        #region Wireframe mode
        //Breshham�㷨����,��ɫʹ�����Բ�ֵ����͸��У����
        private void DrawLine(Vector3 begin, Vector3 end, Color colorBegin, Color colorEnd)
        {            
            int x1 = Mathf.FloorToInt(begin.x);
            int y1 = Mathf.FloorToInt(begin.y);
            int x2 = Mathf.FloorToInt(end.x);
            int y2 = Mathf.FloorToInt(end.y);            

            int x, y, dx, dy, dx1, dy1, px, py, xe, ye, i;

            dx = x2 - x1;
            dy = y2 - y1;
            dx1 = Math.Abs(dx);
            dy1 = Math.Abs(dy);
            px = 2 * dy1 - dx1;
            py = 2 * dx1 - dy1;

            Color c1 = colorBegin;
            Color c2 = colorEnd;

            if (dy1 <= dx1)
            {
                if (dx >= 0)
                {
                    x = x1;
                    y = y1;
                    xe = x2;
                }
                else
                {
                    x = x2;
                    y = y2;
                    xe = x1;
                    c1 = colorEnd;
                    c2 = colorBegin;
                }
                Vector3 point = new Vector3(x, y, 1.0f);                 
                SetPixel(point, c1);
                for (i = 0; x < xe; i++)
                {
                    x++;
                    if (px < 0)
                    {
                        px += 2 * dy1;
                    }
                    else
                    {
                        if ((dx < 0 && dy < 0) || (dx > 0 && dy > 0))
                        {
                            y++;
                        }
                        else
                        {
                            y--;
                        }
                        px +=  2 * (dy1 - dx1);
                    }
                    
                    Vector3 pt = new Vector3(x, y, 1.0f);
                    float t = 1.0f - (float)(xe - x) / dx1;
                    Color line_color = Color.Lerp(c1, c2, t);                    
                    SetPixel(pt, line_color);
                }
            }
            else
            {
                if (dy >= 0)
                {
                    x = x1;
                    y = y1;
                    ye = y2;
                }
                else
                {
                    x = x2;
                    y = y2;
                    ye = y1;
                    c1 = colorEnd;
                    c2 = colorBegin;
                }
                Vector3 point = new Vector3(x, y, 1.0f);                
                SetPixel(point, c1);
                
                for (i = 0; y < ye; i++)
                {
                    y++;
                    if (py <= 0)
                    {
                        py += 2 * dx1;
                    }
                    else
                    {
                        if ((dx < 0 && dy < 0) || (dx > 0 && dy > 0))
                        {
                            x++;
                        }
                        else
                        {
                            x--;
                        }
                        py += 2 * (dx1 - dy1);
                    }
                    Vector3 pt = new Vector3(x, y, 1.0f);
                    float t = 1.0f - (float)(ye - y) / dy1;
                    Color line_color = Color.Lerp(c1, c2, t);
                    SetPixel(pt, line_color);
                }
            }
        }

        private void RasterizeWireframe(Triangle t)
        {
            DrawLine(t.Positions[0], t.Positions[1], t.Colors[0], t.Colors[1]);
            DrawLine(t.Positions[1], t.Positions[2], t.Colors[1], t.Colors[2]);
            DrawLine(t.Positions[2], t.Positions[0], t.Colors[2], t.Colors[0]);
        }

        #endregion

        

        //Screen space  rasterization
        void RasterizeTriangle(Triangle t, RenderingObject ro)
        {
            var v = t.Positions;
            
            //Find out the bounding box of current triangle.
            float minX = v[0].x;
            float maxX = minX;
            float minY = v[0].y;
            float maxY = minY;

            for(int i=1; i<3; ++i)
            {
                float x = v[i].x;
                if(x < minX)
                {
                    minX = x;
                } else if(x > maxX)
                {
                    maxX = x;
                }
                float y = v[i].y;
                if(y < minY)
                {
                    minY = y;
                }else if(y > maxY)
                {
                    maxY = y;
                }
            }

            int minPX = Mathf.FloorToInt(minX);
            minPX = minPX < 0 ? 0 : minPX;
            int maxPX = Mathf.CeilToInt(maxX);
            maxPX = maxPX > _width ? _width : maxPX;
            int minPY = Mathf.FloorToInt(minY);
            minPY = minPY < 0 ? 0 : minPY;
            int maxPY = Mathf.CeilToInt(maxY);
            maxPY = maxPY > _height ? _height : maxPY;

            if(_config.MSAA == MSAALevel.Disabled)
            {                
                // ������ǰ�����ΰ�Χ�е��������أ��жϵ�ǰ�����Ƿ�����������
                // �������������е����أ�ʹ�����������ֵ�õ����ֵ����ʹ��z buffer������Ȳ��Ժ�д��
                for(int y = minPY; y < maxPY; ++y)
                {
                    for(int x = minPX; x < maxPX; ++x)
                    {
                        if(IsInsideTriangle(x, y, t))
                        {
                            //������������
                            var c = ComputeBarycentric2D(x, y, t);
                            float alpha = c.x;
                            float beta = c.y;
                            float gamma = c.z;
                            //͸��У����ֵ��zΪ͸��У����ֵ���view space zֵ
                            float z = 1.0f / (alpha / v[0].w + beta / v[1].w + gamma / v[2].w);
                            //zpΪ͸��У����ֵ���screen space zֵ
                            float zp = (alpha * v[0].z / v[0].w + beta * v[1].z / v[1].w + gamma * v[2].z / v[2].w) * z;
                            
                            //��Ȳ���(ע�����������zֵԽ��Խ����near plane����˴�ֵͨ�����ԣ�
                            int index = GetIndex(x, y);
                            if(zp >= depth_buf[index])
                            {
                                depth_buf[index] = zp;

                                //͸��У����ֵ
                                Color color_p = (alpha * t.Colors[0] / v[0].w + beta * t.Colors[1] / v[1].w + gamma * t.Colors[2] / v[2].w) * z;
                                Vector2 uv_p = (alpha * t.TexCoords[0] / v[0].w + beta * t.TexCoords[1] / v[1].w + gamma * t.TexCoords[2] / v[2].w) * z;
                                Vector3 normal_p = (alpha * t.Normals[0] / v[0].w + beta * t.Normals[1] / v[1].w + gamma * t.Normals[2] / v[2].w) * z;
                                Vector3 worldPos_p = (alpha * t.WorldPoses[0] / v[0].w + beta * t.WorldPoses[1] / v[1].w + gamma * t.WorldPoses[2] / v[2].w) * z;
                                Vector3 worldNormal_p = (alpha * t.WorldNormals[0] / v[0].w + beta * t.WorldNormals[1] / v[1].w + gamma * t.WorldNormals[2] / v[2].w) * z;

                                if (CurrentFragmentShader != null)
                                {
                                    FragmentShaderInputData input = new FragmentShaderInputData();
                                    input.Color = color_p;
                                    input.UV = uv_p;
                                    input.Texture = ro.texture;
                                    input.LocalNormal = normal_p;
                                    input.WorldPos = worldPos_p;
                                    input.WorldNormal = worldNormal_p;

                                    frame_buf[index] = CurrentFragmentShader(input);
                                }
                                

                                
                            }
                        }                        
                    }
                }
            }
            else
            {
                int MSAALevel = (int)_config.MSAA;
                float sampler_dis = 1.0f / MSAALevel;
                float sampler_dis_half = sampler_dis * 0.5f;

                for (int y = minPY; y < maxPY; ++y)
                {
                    for (int x = minPX; x < maxPX; ++x)
                    {
                        //���ÿ���������Ƿ����������ڣ�����ڽ������������ֵ����Ȳ���
                        for(int si=0; si<MSAALevel; ++si)
                        {
                            for(int sj=0; sj<MSAALevel; ++sj)
                            {
                                float offsetx = sampler_dis_half + si * sampler_dis;
                                float offsety = sampler_dis_half + sj * sampler_dis;
                                if (IsInsideTriangle(x, y, t, offsetx, offsety))
                                {
                                    //������������
                                    var c = ComputeBarycentric2D(x+offsetx, y+offsety, t);
                                    float alpha = c.x;
                                    float beta = c.y;
                                    float gamma = c.z;
                                    //͸��У����ֵ��zΪ͸��У����ֵ���view space zֵ
                                    float z = 1.0f / (alpha / v[0].w + beta / v[1].w + gamma / v[2].w);
                                    //zpΪ͸��У����ֵ���screen space zֵ
                                    float zp = (alpha * v[0].z / v[0].w + beta * v[1].z / v[1].w + gamma * v[2].z / v[2].w) * z;

                                    //��Ȳ���(ע�����������zֵԽ��Խ����near plane����˴�ֵͨ�����ԣ�                                    
                                    int xi = x * MSAALevel + si;
                                    int yi = y * MSAALevel + sj;
                                    int index = yi * _width * MSAALevel + xi;
                                    if (zp > samplers_depth_MSAA[index])
                                    {
                                        samplers_depth_MSAA[index] = zp;
                                        samplers_mask_MSAA[index] = true;

                                        //͸��У����ֵ
                                        Color color_p = (alpha * t.Colors[0] / v[0].w + beta * t.Colors[1] / v[1].w + gamma * t.Colors[2] / v[2].w) * z;
                                        samplers_color_MSAA[index] = color_p;
                                    }
                                }
                            }
                        }
                        
                    }
                }
            }
        }

        bool IsInsideTriangle(int x, int y, Triangle t, float offsetX=0.5f, float offsetY=0.5f)
        {
            Vector3[] v = new Vector3[3];
            for(int i=0; i<3; ++i)
            {
                v[i] = new Vector3(t.Positions[i].x, t.Positions[i].y, t.Positions[i].z);
            }

            //��ǰ��������λ��p
            Vector3 p = new Vector3(x + offsetX, y + offsetY, 0);            
            
            Vector3 v0p = p - v[0]; v0p[2] = 0;
            Vector3 v01 = v[1] - v[0]; v01[2] = 0;
            Vector3 cross0p = Vector3.Cross(v0p, v01);

            Vector3 v1p = p - v[1]; v1p[2] = 0;
            Vector3 v12 = v[2] - v[1]; v12[2] = 0;
            Vector3 cross1p = Vector3.Cross(v1p, v12);

            if(cross0p.z * cross1p.z > 0)
            {
                Vector3 v2p = p - v[2]; v2p[2] = 0;
                Vector3 v20 = v[0] - v[2]; v20[2] = 0;
                Vector3 cross2p = Vector3.Cross(v2p, v20);
                if(cross2p.z * cross1p.z > 0)
                {
                    return true;
                }
            }

            return false;
        }

        Vector3 ComputeBarycentric2D(float x, float y, Triangle t)
        {
            var v = t.Positions;
            float c1 = (x * (v[1].y - v[2].y) + (v[2].x - v[1].x) * y + v[1].x * v[2].y - v[2].x * v[1].y) / (v[0].x * (v[1].y - v[2].y) + (v[2].x - v[1].x) * v[0].y + v[1].x * v[2].y - v[2].x * v[1].y);
            float c2 = (x * (v[2].y - v[0].y) + (v[0].x - v[2].x) * y + v[2].x * v[0].y - v[0].x * v[2].y) / (v[1].x * (v[2].y - v[0].y) + (v[0].x - v[2].x) * v[1].y + v[2].x * v[0].y - v[0].x * v[2].y);
            float c3 = (x * (v[0].y - v[1].y) + (v[1].x - v[0].x) * y + v[0].x * v[1].y - v[1].x * v[0].y) / (v[2].x * (v[0].y - v[1].y) + (v[1].x - v[0].x) * v[2].y + v[0].x * v[1].y - v[1].x * v[0].y);
            return new Vector3(c1, c2, c3);
        }

        public int GetIndex(int x, int y)
        {
            return y * _width + x;
        }

        public void SetPixel(Vector3 point, Color color)
        {
            if(point.x < 0 || point.x >= _width || point.y < 0 || point.y >= _height)
            {
                return;
            }

            int idx = (int)point.y * _width + (int)point.x;
            frame_buf[idx] = color;
        }

        public void UpdateFrame()
        {
            switch (_config.DisplayBuffer)
            {
                case DisplayBufferType.Color:
                    texture.SetPixels(frame_buf);
                    break;
                case DisplayBufferType.DepthRed:
                case DisplayBufferType.DepthGray:
                    for (int i = 0; i < depth_buf.Length; ++i)
                    {
                        //depth_buf�е�ֵ��Χ��[0,1]���������Ϊ1����Զ��Ϊ0����˿��ӻ��󱳾��Ǻ�ɫ
                        float c = depth_buf[i]; 
                        if(_config.DisplayBuffer == DisplayBufferType.DepthRed)
                        {
                            temp_buf[i] = new Color(c, 0, 0);
                        }
                        else
                        {
                            temp_buf[i] = new Color(c, c, c);
                        }                        
                    }
                    texture.SetPixels(temp_buf);
                    break;
            }                                
            
            texture.Apply();

            if (StatDelegate != null)
            {
                StatDelegate(_verticesAll, _trianglesAll, _trianglesRendered);
            }
        }


    }
}